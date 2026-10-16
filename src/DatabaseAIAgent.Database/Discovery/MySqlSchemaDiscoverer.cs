using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Dapper;

namespace DatabaseAIAgent.Database.Discovery;

/// <summary>
/// Reads MySQL information_schema for the connected database. MySQL only shows objects the account has privileges
/// on, so a procedure-only account sees its routines but not the underlying tables. Never reads table rows.
/// </summary>
internal sealed class MySqlSchemaDiscoverer : ISchemaDiscoverer
{
    public DatabaseProviderKind Kind => DatabaseProviderKind.MySql;

    public async Task<DiscoveredDatabase> DiscoverAsync(DbConnection connection, DiscoveryLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var warnings = new List<string>();
        CommandDefinition Cmd(string sql, object? args = null) => new(sql, args, commandTimeout: limits.CommandTimeoutSeconds, cancellationToken: cancellationToken);

        var database = await connection.ExecuteScalarAsync<string>(Cmd("SELECT DATABASE()")) ?? string.Empty;

        var tableRows = (await connection.QueryAsync<TableRow>(Cmd("""
            SELECT TABLE_NAME AS Name, TABLE_TYPE AS Type, NULLIF(TABLE_COMMENT, '') AS Description, CAST(TABLE_ROWS AS SIGNED) AS EstimatedRows
            FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME LIMIT @take
            """, new { take = limits.MaxTables + 1 }))).ToList();
        if (tableRows.Count > limits.MaxTables)
        {
            tableRows.RemoveAt(tableRows.Count - 1);
            warnings.Add($"Only the first {limits.MaxTables} tables and views were analyzed.");
        }

        var columns = (await connection.QueryAsync<ColumnRow>(Cmd("""
            SELECT TABLE_NAME AS TableName, COLUMN_NAME AS Name, CAST(ORDINAL_POSITION AS SIGNED) AS Ordinal, COLUMN_TYPE AS DataType,
                   CAST(CHARACTER_MAXIMUM_LENGTH AS SIGNED) AS MaxLength, CAST(NUMERIC_PRECISION AS SIGNED) AS `Precision`,
                   CAST(NUMERIC_SCALE AS SIGNED) AS Scale, IS_NULLABLE = 'YES' AS IsNullable, COLUMN_KEY = 'PRI' AS IsPrimaryKey,
                   EXTRA LIKE '%auto_increment%' AS IsIdentity, NULLIF(COLUMN_COMMENT, '') AS Description
            FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
            """))).ToLookup(c => c.TableName, StringComparer.Ordinal);

        var indexes = (await connection.QueryAsync<IndexRow>(Cmd("""
            SELECT TABLE_NAME AS TableName, INDEX_NAME AS Name, NON_UNIQUE = 0 AS IsUnique, COLUMN_NAME AS ColumnName, CAST(SEQ_IN_INDEX AS SIGNED) AS Sequence
            FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
            """))).ToLookup(i => i.TableName, StringComparer.Ordinal);

        var foreignKeys = (await connection.QueryAsync<ForeignKeyRow>(Cmd("""
            SELECT CONSTRAINT_NAME AS Name, TABLE_NAME AS FromTable, COLUMN_NAME AS FromColumn, REFERENCED_TABLE_SCHEMA AS ToSchema,
                   REFERENCED_TABLE_NAME AS ToTable, REFERENCED_COLUMN_NAME AS ToColumn, CAST(ORDINAL_POSITION AS SIGNED) AS Ordinal
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL
            """))).ToList();

        var routineRows = (await connection.QueryAsync<RoutineRow>(Cmd("""
            SELECT ROUTINE_NAME AS Name, ROUTINE_TYPE AS Type, NULLIF(ROUTINE_COMMENT, '') AS Description, ROUTINE_DEFINITION AS Definition,
                   DTD_IDENTIFIER AS ReturnType, SQL_DATA_ACCESS AS DataAccess
            FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE() ORDER BY ROUTINE_NAME LIMIT @take
            """, new { take = limits.MaxProcedures + 1 }))).ToList();
        if (routineRows.Count > limits.MaxProcedures)
        {
            routineRows.RemoveAt(routineRows.Count - 1);
            warnings.Add($"Only the first {limits.MaxProcedures} stored procedures and functions were analyzed.");
        }

        var parameters = (await connection.QueryAsync<ParameterRow>(Cmd("""
            SELECT SPECIFIC_NAME AS RoutineName, CAST(ORDINAL_POSITION AS SIGNED) AS Ordinal, PARAMETER_MODE AS Mode, PARAMETER_NAME AS Name,
                   DTD_IDENTIFIER AS DataType, CAST(CHARACTER_MAXIMUM_LENGTH AS SIGNED) AS MaxLength,
                   CAST(NUMERIC_PRECISION AS SIGNED) AS `Precision`, CAST(NUMERIC_SCALE AS SIGNED) AS Scale
            FROM information_schema.PARAMETERS WHERE SPECIFIC_SCHEMA = DATABASE() AND ORDINAL_POSITION > 0
            """))).ToLookup(p => p.RoutineName, StringComparer.Ordinal);

        if (tableRows.Count == 0 && routineRows.Count > 0)
        {
            warnings.Add("No tables are visible to this account (expected for a procedure-only account); table metadata is limited.");
        }

        var tables = tableRows.Select(t => new DiscoveredTable(
            database,
            t.Name,
            t.Type == "VIEW" ? TableKind.View : TableKind.Table,
            t.Description,
            t.EstimatedRows,
            columns[t.Name].Select(c => new DiscoveredColumn(
                c.Name, (int)c.Ordinal, c.DataType, ToInt(c.MaxLength), ToInt(c.Precision), ToInt(c.Scale),
                c.IsNullable != 0, c.IsPrimaryKey != 0, c.IsIdentity != 0, c.Description)).ToList(),
            indexes[t.Name].GroupBy(i => i.Name).Select(g => new DiscoveredIndex(
                g.Key, g.First().IsUnique != 0, g.Key == "PRIMARY", g.OrderBy(i => i.Sequence).Select(i => i.ColumnName).ToList())).ToList()))
            .ToList();

        var relationships = foreignKeys
            .GroupBy(f => (f.Name, f.FromTable))
            .Select(g => new DiscoveredRelationship(
                g.Key.Name, database, g.Key.FromTable, g.OrderBy(f => f.Ordinal).Select(f => f.FromColumn).ToList(),
                g.First().ToSchema, g.First().ToTable, g.OrderBy(f => f.Ordinal).Select(f => f.ToColumn).ToList()))
            .ToList();

        var routines = routineRows.Select(r => new DiscoveredRoutine(
            database,
            r.Name,
            string.Empty,
            r.Type == "PROCEDURE" ? RoutineKind.Procedure : RoutineKind.ScalarFunction,
            r.Description,
            r.Type == "FUNCTION" ? r.ReturnType : null,
            CanExecute: true, // information_schema lists only routines this account may use
            r.Definition,
            r.DataAccess,
            null,
            parameters[r.Name].OrderBy(p => p.Ordinal).Select(p => new DiscoveredParameter(
                p.Name ?? $"arg{p.Ordinal}", (int)p.Ordinal, p.DataType, ToInt(p.MaxLength), ToInt(p.Precision), ToInt(p.Scale),
                p.Mode switch { "OUT" => ParameterDirection.Out, "INOUT" => ParameterDirection.InOut, _ => ParameterDirection.In },
                HasDefault: false)).ToList(),
            [])).ToList();

        return new DiscoveredDatabase(database.Length > 0 ? [database] : [], tables, relationships, routines, warnings);
    }

    private static int? ToInt(long? value) => value is null ? null : (int)Math.Min(value.Value, int.MaxValue);

    private sealed class TableRow
    {
        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string? Description { get; set; }

        public long? EstimatedRows { get; set; }
    }

    private sealed class ColumnRow
    {
        public string TableName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public long Ordinal { get; set; }

        public string DataType { get; set; } = string.Empty;

        public long? MaxLength { get; set; }

        public long? Precision { get; set; }

        public long? Scale { get; set; }

        public long IsNullable { get; set; }

        public long IsPrimaryKey { get; set; }

        public long IsIdentity { get; set; }

        public string? Description { get; set; }
    }

    private sealed class IndexRow
    {
        public string TableName { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public long IsUnique { get; set; }

        public string ColumnName { get; set; } = string.Empty;

        public long Sequence { get; set; }
    }

    private sealed class ForeignKeyRow
    {
        public string Name { get; set; } = string.Empty;

        public string FromTable { get; set; } = string.Empty;

        public string FromColumn { get; set; } = string.Empty;

        public string ToSchema { get; set; } = string.Empty;

        public string ToTable { get; set; } = string.Empty;

        public string ToColumn { get; set; } = string.Empty;

        public long Ordinal { get; set; }
    }

    private sealed class RoutineRow
    {
        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? Definition { get; set; }

        public string? ReturnType { get; set; }

        public string? DataAccess { get; set; }
    }

    private sealed class ParameterRow
    {
        public string RoutineName { get; set; } = string.Empty;

        public long Ordinal { get; set; }

        public string? Mode { get; set; }

        public string? Name { get; set; }

        public string DataType { get; set; } = string.Empty;

        public long? MaxLength { get; set; }

        public long? Precision { get; set; }

        public long? Scale { get; set; }
    }
}
