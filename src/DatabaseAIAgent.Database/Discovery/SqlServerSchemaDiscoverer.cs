using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Dapper;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Database.Discovery;

/// <summary>Reads SQL Server catalog views (sys.*). Requires only VIEW DEFINITION; never reads table rows.</summary>
internal sealed class SqlServerSchemaDiscoverer(ILogger<SqlServerSchemaDiscoverer> logger) : ISchemaDiscoverer
{
    private static readonly HashSet<string> CharacterTypes = new(StringComparer.OrdinalIgnoreCase) { "char", "varchar", "nchar", "nvarchar", "binary", "varbinary" };
    private static readonly HashSet<string> UnicodeTypes = new(StringComparer.OrdinalIgnoreCase) { "nchar", "nvarchar" };
    private static readonly HashSet<string> DecimalTypes = new(StringComparer.OrdinalIgnoreCase) { "decimal", "numeric" };

    public DatabaseProviderKind Kind => DatabaseProviderKind.SqlServer;

    public async Task<DiscoveredDatabase> DiscoverAsync(DbConnection connection, DiscoveryLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var warnings = new List<string>();
        CommandDefinition Cmd(string sql, object? args = null) => new(sql, args, commandTimeout: limits.CommandTimeoutSeconds, cancellationToken: cancellationToken);

        var schemas = (await connection.QueryAsync<string>(Cmd(
            "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest') ORDER BY name"))).ToList();

        var tableRows = (await connection.QueryAsync<TableRow>(Cmd("""
            SELECT TOP (@take) o.object_id AS ObjectId, SCHEMA_NAME(o.schema_id) AS SchemaName, o.name AS Name, o.type AS Type,
                   CAST(ep.value AS nvarchar(2000)) AS Description,
                   (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)) AS EstimatedRows
            FROM sys.objects o
            LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
            WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY SchemaName, Name
            """, new { take = limits.MaxTables + 1 }))).ToList();
        if (tableRows.Count > limits.MaxTables)
        {
            tableRows.RemoveAt(tableRows.Count - 1);
            warnings.Add($"Only the first {limits.MaxTables} tables and views were analyzed.");
        }

        var tableIds = tableRows.Select(t => t.ObjectId).ToHashSet();
        var columns = (await connection.QueryAsync<ColumnRow>(Cmd("""
            SELECT c.object_id AS ObjectId, c.column_id AS Ordinal, c.name AS Name, TYPE_NAME(c.user_type_id) AS DataType,
                   CAST(c.max_length AS int) AS MaxLength, CAST(c.precision AS int) AS Precision, CAST(c.scale AS int) AS Scale,
                   c.is_nullable AS IsNullable, c.is_identity AS IsIdentity, CAST(ep.value AS nvarchar(1000)) AS Description
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
            WHERE o.type IN ('U', 'V', 'IF', 'TF') AND o.is_ms_shipped = 0
            """))).ToList();

        var indexes = (await connection.QueryAsync<IndexRow>(Cmd("""
            SELECT i.object_id AS ObjectId, i.index_id AS IndexId, i.name AS Name, i.is_unique AS IsUnique, i.is_primary_key AS IsPrimaryKey,
                   c.name AS ColumnName, CAST(ic.key_ordinal AS int) AS KeyOrdinal
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.objects o ON o.object_id = i.object_id
            WHERE o.type = 'U' AND o.is_ms_shipped = 0 AND i.type > 0 AND ic.is_included_column = 0 AND i.name IS NOT NULL
            """))).Where(i => tableIds.Contains(i.ObjectId)).ToList();

        var foreignKeys = (await connection.QueryAsync<ForeignKeyRow>(Cmd("""
            SELECT fk.name AS Name, OBJECT_SCHEMA_NAME(fk.parent_object_id) AS FromSchema, OBJECT_NAME(fk.parent_object_id) AS FromTable, pc.name AS FromColumn,
                   OBJECT_SCHEMA_NAME(fk.referenced_object_id) AS ToSchema, OBJECT_NAME(fk.referenced_object_id) AS ToTable, rc.name AS ToColumn,
                   fkc.constraint_column_id AS Ordinal
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            """))).ToList();

        var routineRows = (await connection.QueryAsync<RoutineRow>(Cmd("""
            SELECT TOP (@take) o.object_id AS ObjectId, SCHEMA_NAME(o.schema_id) AS SchemaName, o.name AS Name, o.type AS Type,
                   CAST(ep.value AS nvarchar(2000)) AS Description, OBJECT_DEFINITION(o.object_id) AS Definition,
                   CAST(ISNULL(HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name), 'OBJECT',
                        CASE WHEN o.type IN ('IF', 'TF') THEN 'SELECT' ELSE 'EXECUTE' END), 0) AS bit) AS CanExecute
            FROM sys.objects o
            LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0 AND ep.name = 'MS_Description'
            WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
            ORDER BY SchemaName, Name
            """, new { take = limits.MaxProcedures + 1 }))).ToList();
        if (routineRows.Count > limits.MaxProcedures)
        {
            routineRows.RemoveAt(routineRows.Count - 1);
            warnings.Add($"Only the first {limits.MaxProcedures} stored procedures and functions were analyzed.");
        }

        var routineIds = routineRows.Select(r => r.ObjectId).ToHashSet();
        var parameters = (await connection.QueryAsync<ParameterRow>(Cmd("""
            SELECT p.object_id AS ObjectId, p.parameter_id AS Ordinal, p.name AS Name, TYPE_NAME(p.user_type_id) AS DataType,
                   CAST(p.max_length AS int) AS MaxLength, CAST(p.precision AS int) AS Precision, CAST(p.scale AS int) AS Scale,
                   p.is_output AS IsOutput, p.has_default_value AS HasDefault
            FROM sys.parameters p
            JOIN sys.objects o ON o.object_id = p.object_id
            WHERE o.type IN ('P', 'FN', 'IF', 'TF') AND o.is_ms_shipped = 0
            """))).Where(p => routineIds.Contains(p.ObjectId)).ToLookup(p => p.ObjectId);

        var resultColumns = await TryAsync(warnings, "Result columns of some procedures could not be described.", async () =>
            (await connection.QueryAsync<ResultRow>(Cmd("""
                SELECT p.object_id AS ObjectId, CAST(r.column_ordinal AS int) AS Ordinal, r.name AS Name, r.system_type_name AS DataType, ISNULL(r.is_nullable, 1) AS IsNullable
                FROM sys.procedures p
                CROSS APPLY sys.dm_exec_describe_first_result_set_for_object(p.object_id, 0) r
                WHERE p.is_ms_shipped = 0 AND r.error_number IS NULL AND r.is_hidden = 0
                """))).ToLookup(r => r.ObjectId))
            ?? Enumerable.Empty<ResultRow>().ToLookup(r => r.ObjectId);

        // Needs VIEW SERVER STATE; optional.
        var durations = await TryAsync(warnings, null, async () =>
            (await connection.QueryAsync<(int ObjectId, double AverageMs)>(Cmd("""
                SELECT object_id, CAST(total_elapsed_time AS float) / NULLIF(execution_count, 0) / 1000.0
                FROM sys.dm_exec_procedure_stats WHERE database_id = DB_ID()
                """))).GroupBy(d => d.ObjectId).ToDictionary(g => g.Key, g => g.Average(x => x.AverageMs)))
            ?? [];

        var columnsByObject = columns.ToLookup(c => c.ObjectId);
        var primaryKeyColumns = indexes.Where(i => i.IsPrimaryKey).Select(i => (i.ObjectId, i.ColumnName)).ToHashSet();

        var tables = tableRows.Select(t => new DiscoveredTable(
            t.SchemaName,
            t.Name,
            t.Type.Trim() == "V" ? TableKind.View : TableKind.Table,
            t.Description,
            t.EstimatedRows,
            columnsByObject[t.ObjectId].Select(c => new DiscoveredColumn(
                c.Name,
                c.Ordinal,
                c.DataType,
                Length(c.DataType, c.MaxLength),
                DecimalTypes.Contains(c.DataType) ? c.Precision : null,
                DecimalTypes.Contains(c.DataType) ? c.Scale : null,
                c.IsNullable,
                primaryKeyColumns.Contains((t.ObjectId, c.Name)),
                c.IsIdentity,
                c.Description)).ToList(),
            indexes.Where(i => i.ObjectId == t.ObjectId)
                .GroupBy(i => i.IndexId)
                .Select(g => new DiscoveredIndex(g.First().Name, g.First().IsUnique, g.First().IsPrimaryKey, g.OrderBy(i => i.KeyOrdinal).Select(i => i.ColumnName).ToList()))
                .ToList())).ToList();

        var relationships = foreignKeys
            .GroupBy(f => (f.Name, f.FromSchema, f.FromTable))
            .Select(g => new DiscoveredRelationship(
                g.Key.Name, g.Key.FromSchema, g.Key.FromTable, g.OrderBy(f => f.Ordinal).Select(f => f.FromColumn).ToList(),
                g.First().ToSchema, g.First().ToTable, g.OrderBy(f => f.Ordinal).Select(f => f.ToColumn).ToList()))
            .ToList();

        var routines = routineRows.Select(r =>
        {
            var type = r.Type.Trim();
            var defaults = RoutineDefinitionAnalyzer.SqlServerParametersWithDefaults(r.Definition);
            var routineParameters = parameters[r.ObjectId].ToList();
            var returnValue = routineParameters.FirstOrDefault(p => p.Ordinal == 0);
            var kind = type switch { "P" => RoutineKind.Procedure, "FN" => RoutineKind.ScalarFunction, _ => RoutineKind.TableFunction };
            var results = kind == RoutineKind.TableFunction
                ? columnsByObject[r.ObjectId].Select(c => new DiscoveredResultColumn(c.Name, c.Ordinal, c.DataType, c.IsNullable)).ToList()
                : resultColumns[r.ObjectId].Select(c => new DiscoveredResultColumn(c.Name ?? $"column{c.Ordinal}", c.Ordinal, c.DataType, c.IsNullable)).ToList();

            return new DiscoveredRoutine(
                r.SchemaName,
                r.Name,
                string.Empty,
                kind,
                r.Description,
                returnValue?.DataType,
                r.CanExecute,
                r.Definition,
                null,
                durations.TryGetValue(r.ObjectId, out var ms) ? ms : null,
                routineParameters.Where(p => p.Ordinal > 0).Select(p => new DiscoveredParameter(
                    p.Name.TrimStart('@'),
                    p.Ordinal,
                    p.DataType,
                    Length(p.DataType, p.MaxLength),
                    DecimalTypes.Contains(p.DataType) ? p.Precision : null,
                    DecimalTypes.Contains(p.DataType) ? p.Scale : null,
                    p.IsOutput ? ParameterDirection.InOut : ParameterDirection.In,
                    p.HasDefault || defaults.Contains(p.Name))).ToList(),
                results);
        }).ToList();

        if (routineRows.Count > 0 && routineRows.All(r => r.Definition is null))
        {
            warnings.Add("Procedure definitions are not visible to this account (grant VIEW DEFINITION), so read/write behaviour could not be verified.");
        }

        return new DiscoveredDatabase(schemas, tables, relationships, routines, warnings);
    }

    /// <summary>Characters for text types (n-types are 2 bytes per character); -1 = max; null for other types.</summary>
    private static int? Length(string dataType, int maxLength) =>
        !CharacterTypes.Contains(dataType) ? null
        : maxLength == -1 ? -1
        : UnicodeTypes.Contains(dataType) ? maxLength / 2 : maxLength;

    private async Task<T?> TryAsync<T>(List<string> warnings, string? warning, Func<Task<T>> query)
        where T : class
    {
        try
        {
            return await query();
        }
        catch (DbException ex)
        {
            logger.LogInformation("Optional SQL Server metadata query skipped ({ErrorType})", ex.GetType().Name);
            if (warning is not null)
            {
                warnings.Add(warning);
            }

            return null;
        }
    }

    private sealed class TableRow
    {
        public int ObjectId { get; set; }

        public string SchemaName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string? Description { get; set; }

        public long? EstimatedRows { get; set; }
    }

    private sealed class ColumnRow
    {
        public int ObjectId { get; set; }

        public int Ordinal { get; set; }

        public string Name { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;

        public int MaxLength { get; set; }

        public int Precision { get; set; }

        public int Scale { get; set; }

        public bool IsNullable { get; set; }

        public bool IsIdentity { get; set; }

        public string? Description { get; set; }
    }

    private sealed class IndexRow
    {
        public int ObjectId { get; set; }

        public int IndexId { get; set; }

        public string Name { get; set; } = string.Empty;

        public bool IsUnique { get; set; }

        public bool IsPrimaryKey { get; set; }

        public string ColumnName { get; set; } = string.Empty;

        public int KeyOrdinal { get; set; }
    }

    private sealed class ForeignKeyRow
    {
        public string Name { get; set; } = string.Empty;

        public string FromSchema { get; set; } = string.Empty;

        public string FromTable { get; set; } = string.Empty;

        public string FromColumn { get; set; } = string.Empty;

        public string ToSchema { get; set; } = string.Empty;

        public string ToTable { get; set; } = string.Empty;

        public string ToColumn { get; set; } = string.Empty;

        public int Ordinal { get; set; }
    }

    private sealed class RoutineRow
    {
        public int ObjectId { get; set; }

        public string SchemaName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? Definition { get; set; }

        public bool CanExecute { get; set; }
    }

    private sealed class ParameterRow
    {
        public int ObjectId { get; set; }

        public int Ordinal { get; set; }

        public string Name { get; set; } = string.Empty;

        public string DataType { get; set; } = string.Empty;

        public int MaxLength { get; set; }

        public int Precision { get; set; }

        public int Scale { get; set; }

        public bool IsOutput { get; set; }

        public bool HasDefault { get; set; }
    }

    private sealed class ResultRow
    {
        public int ObjectId { get; set; }

        public int Ordinal { get; set; }

        public string? Name { get; set; }

        public string DataType { get; set; } = string.Empty;

        public bool IsNullable { get; set; }
    }
}
