using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Dapper;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Database.Discovery;

/// <summary>
/// Reads PostgreSQL system catalogs (pg_class, pg_attribute, pg_proc, ...), which every role can read, so a
/// least-privilege account still sees full metadata. Extension-owned objects are excluded. Never reads table rows.
/// </summary>
internal sealed class PostgresSchemaDiscoverer(ILogger<PostgresSchemaDiscoverer> logger) : ISchemaDiscoverer
{
    private const string UserNamespaces = "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND n.nspname NOT LIKE 'pg_temp%'";

    public DatabaseProviderKind Kind => DatabaseProviderKind.PostgreSql;

    public async Task<DiscoveredDatabase> DiscoverAsync(DbConnection connection, DiscoveryLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var warnings = new List<string>();
        CommandDefinition Cmd(string sql, object? args = null) => new(sql, args, commandTimeout: limits.CommandTimeoutSeconds, cancellationToken: cancellationToken);

        var schemas = (await connection.QueryAsync<string>(Cmd($"SELECT n.nspname FROM pg_namespace n WHERE {UserNamespaces} ORDER BY 1"))).ToList();

        var tableRows = (await connection.QueryAsync<TableRow>(Cmd($"""
            SELECT c.oid::bigint AS oid, n.nspname AS schema_name, c.relname AS name, c.relkind::text AS kind,
                   obj_description(c.oid, 'pg_class') AS description,
                   CASE WHEN c.reltuples >= 0 THEN c.reltuples::bigint END AS estimated_rows
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm') AND NOT c.relispartition AND {UserNamespaces}
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e')
            ORDER BY 2, 3
            LIMIT @take
            """, new { take = limits.MaxTables + 1 }))).ToList();
        if (tableRows.Count > limits.MaxTables)
        {
            tableRows.RemoveAt(tableRows.Count - 1);
            warnings.Add($"Only the first {limits.MaxTables} tables and views were analyzed.");
        }

        var oids = tableRows.Select(t => t.oid).ToArray();
        var columns = (await connection.QueryAsync<ColumnRow>(Cmd("""
            SELECT a.attrelid::bigint AS oid, a.attnum::int AS ordinal, a.attname AS name, format_type(a.atttypid, a.atttypmod) AS data_type,
                   t.typname AS base_type, a.atttypmod AS typmod, NOT a.attnotnull AS is_nullable,
                   (a.attidentity <> '' OR COALESCE(pg_get_expr(ad.adbin, ad.adrelid), '') LIKE 'nextval(%') AS is_identity,
                   col_description(a.attrelid, a.attnum) AS description
            FROM pg_attribute a
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef ad ON ad.adrelid = a.attrelid AND ad.adnum = a.attnum
            WHERE a.attnum > 0 AND NOT a.attisdropped AND a.attrelid::bigint = ANY(@oids)
            """, new { oids }))).ToLookup(c => c.oid);

        var indexes = (await connection.QueryAsync<IndexRow>(Cmd("""
            SELECT i.indrelid::bigint AS oid, ic.relname AS name, i.indisunique AS is_unique, i.indisprimary AS is_primary,
                   ARRAY(SELECT a.attname::text FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum, ord)
                         JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum ORDER BY k.ord) AS columns
            FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid
            WHERE i.indrelid::bigint = ANY(@oids)
            """, new { oids }))).ToLookup(i => i.oid);

        var foreignKeys = (await connection.QueryAsync<ForeignKeyRow>(Cmd("""
            SELECT con.conname AS name, fn.nspname AS from_schema, fc.relname AS from_table,
                   ARRAY(SELECT a.attname::text FROM unnest(con.conkey) WITH ORDINALITY k(attnum, ord)
                         JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum ORDER BY k.ord) AS from_columns,
                   tn.nspname AS to_schema, tc.relname AS to_table,
                   ARRAY(SELECT a.attname::text FROM unnest(con.confkey) WITH ORDINALITY k(attnum, ord)
                         JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum ORDER BY k.ord) AS to_columns
            FROM pg_constraint con
            JOIN pg_class fc ON fc.oid = con.conrelid JOIN pg_namespace fn ON fn.oid = fc.relnamespace
            JOIN pg_class tc ON tc.oid = con.confrelid JOIN pg_namespace tn ON tn.oid = tc.relnamespace
            WHERE con.contype = 'f' AND fc.oid::bigint = ANY(@oids)
            """, new { oids }))).ToList();

        var routineRows = (await connection.QueryAsync<RoutineRow>(Cmd($"""
            SELECT p.oid::bigint AS oid, n.nspname AS schema_name, p.proname AS name, pg_get_function_identity_arguments(p.oid) AS signature,
                   p.prokind::text AS kind, p.proretset AS returns_set, format_type(p.prorettype, NULL) AS return_type,
                   obj_description(p.oid, 'pg_proc') AS description, has_function_privilege(p.oid, 'EXECUTE') AS can_execute,
                   CASE WHEN l.lanname IN ('sql', 'plpgsql') THEN p.prosrc END AS definition,
                   CASE p.provolatile WHEN 'i' THEN 'IMMUTABLE' WHEN 's' THEN 'STABLE' ELSE 'VOLATILE' END AS volatility,
                   p.pronargs::int AS input_count, p.pronargdefaults::int AS default_count
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
            WHERE p.prokind IN ('f', 'p') AND {UserNamespaces}
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            ORDER BY 2, 3, 4
            LIMIT @take
            """, new { take = limits.MaxProcedures + 1 }))).ToList();
        if (routineRows.Count > limits.MaxProcedures)
        {
            routineRows.RemoveAt(routineRows.Count - 1);
            warnings.Add($"Only the first {limits.MaxProcedures} functions and procedures were analyzed.");
        }

        var routineOids = routineRows.Select(r => r.oid).ToArray();
        var arguments = (await connection.QueryAsync<ArgumentRow>(Cmd("""
            SELECT p.oid::bigint AS oid, a.ord::int AS ordinal, COALESCE(a.name, '') AS name, format_type(a.type, NULL) AS data_type, COALESCE(a.mode::text, 'i') AS mode
            FROM pg_proc p
            CROSS JOIN LATERAL unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[]), p.proargmodes, p.proargnames) WITH ORDINALITY AS a(type, mode, name, ord)
            WHERE p.oid::bigint = ANY(@routineOids) AND a.type IS NOT NULL
            """, new { routineOids }))).ToLookup(a => a.oid);

        Dictionary<long, double> durations;
        try
        {
            durations = (await connection.QueryAsync<(long Oid, double AverageMs)>(Cmd(
                    "SELECT funcid::bigint, total_time / NULLIF(calls, 0) FROM pg_stat_user_functions WHERE calls > 0")))
                .ToDictionary(d => d.Oid, d => d.AverageMs);
        }
        catch (DbException ex)
        {
            logger.LogInformation("Function statistics unavailable ({ErrorType})", ex.GetType().Name);
            durations = [];
        }

        var tables = tableRows.Select(t =>
        {
            var tableIndexes = indexes[t.oid].ToList();
            var primaryKey = tableIndexes.Where(i => i.is_primary).SelectMany(i => i.columns).ToHashSet(StringComparer.Ordinal);
            return new DiscoveredTable(
                t.schema_name,
                t.name,
                t.kind is "v" or "m" ? TableKind.View : TableKind.Table,
                t.description,
                t.estimated_rows,
                columns[t.oid].Select(c =>
                {
                    var (length, precision, scale) = TypeModifiers(c.base_type, c.typmod);
                    return new DiscoveredColumn(c.name, c.ordinal, c.data_type, length, precision, scale, c.is_nullable, primaryKey.Contains(c.name), c.is_identity, c.description);
                }).ToList(),
                tableIndexes.Select(i => new DiscoveredIndex(i.name, i.is_unique, i.is_primary, i.columns)).ToList());
        }).ToList();

        var relationships = foreignKeys.Select(f => new DiscoveredRelationship(f.name, f.from_schema, f.from_table, f.from_columns, f.to_schema, f.to_table, f.to_columns)).ToList();

        var routines = routineRows.Select(r =>
        {
            var args = arguments[r.oid].OrderBy(a => a.ordinal).ToList();
            var inputs = args.Where(a => a.mode is "i" or "b" or "v").ToList();
            var firstDefaulted = inputs.Count - r.default_count;
            var kind = r.kind == "p" ? RoutineKind.Procedure : r.returns_set || r.return_type == "record" ? RoutineKind.TableFunction : RoutineKind.ScalarFunction;

            var parameters = new List<DiscoveredParameter>();
            for (var i = 0; i < inputs.Count; i++)
            {
                var a = inputs[i];
                parameters.Add(new DiscoveredParameter(
                    string.IsNullOrEmpty(a.name) ? $"arg{i + 1}" : a.name, i + 1, a.data_type, null, null, null,
                    a.mode == "b" ? ParameterDirection.InOut : ParameterDirection.In,
                    HasDefault: i >= firstDefaulted));
            }

            // OUT parameters of procedures are outputs; TABLE/OUT columns of set-returning functions are result columns.
            var outputs = args.Where(a => a.mode is "o" or "t").ToList();
            var results = kind == RoutineKind.Procedure ? [] : outputs.Select((a, i) => new DiscoveredResultColumn(a.name, i + 1, a.data_type, true)).ToList();
            if (kind == RoutineKind.Procedure)
            {
                parameters.AddRange(outputs.Select((a, i) => new DiscoveredParameter(a.name, inputs.Count + i + 1, a.data_type, null, null, null, ParameterDirection.Out, false)));
            }

            return new DiscoveredRoutine(
                r.schema_name, r.name, r.signature, kind, r.description, kind == RoutineKind.Procedure ? null : r.return_type,
                r.can_execute, r.definition, r.volatility, durations.TryGetValue(r.oid, out var ms) ? ms : null, parameters, results);
        }).ToList();

        return new DiscoveredDatabase(schemas, tables, relationships, routines, warnings);
    }

    /// <summary>Decodes atttypmod: varchar/char length, numeric precision/scale.</summary>
    private static (int? Length, int? Precision, int? Scale) TypeModifiers(string baseType, int typmod) => baseType switch
    {
        "varchar" or "bpchar" when typmod > 4 => (typmod - 4, null, null),
        "varchar" or "text" => (-1, null, null),
        "numeric" when typmod > 4 => (null, ((typmod - 4) >> 16) & 0xFFFF, (typmod - 4) & 0xFFFF),
        _ => (null, null, null),
    };

#pragma warning disable IDE1006, CA1707 // Column names as returned by PostgreSQL.
    private sealed class TableRow
    {
        public long oid { get; set; }

        public string schema_name { get; set; } = string.Empty;

        public string name { get; set; } = string.Empty;

        public string kind { get; set; } = string.Empty;

        public string? description { get; set; }

        public long? estimated_rows { get; set; }
    }

    private sealed class ColumnRow
    {
        public long oid { get; set; }

        public int ordinal { get; set; }

        public string name { get; set; } = string.Empty;

        public string data_type { get; set; } = string.Empty;

        public string base_type { get; set; } = string.Empty;

        public int typmod { get; set; }

        public bool is_nullable { get; set; }

        public bool is_identity { get; set; }

        public string? description { get; set; }
    }

    private sealed class IndexRow
    {
        public long oid { get; set; }

        public string name { get; set; } = string.Empty;

        public bool is_unique { get; set; }

        public bool is_primary { get; set; }

        public string[] columns { get; set; } = [];
    }

    private sealed class ForeignKeyRow
    {
        public string name { get; set; } = string.Empty;

        public string from_schema { get; set; } = string.Empty;

        public string from_table { get; set; } = string.Empty;

        public string[] from_columns { get; set; } = [];

        public string to_schema { get; set; } = string.Empty;

        public string to_table { get; set; } = string.Empty;

        public string[] to_columns { get; set; } = [];
    }

    private sealed class RoutineRow
    {
        public long oid { get; set; }

        public string schema_name { get; set; } = string.Empty;

        public string name { get; set; } = string.Empty;

        public string signature { get; set; } = string.Empty;

        public string kind { get; set; } = string.Empty;

        public bool returns_set { get; set; }

        public string return_type { get; set; } = string.Empty;

        public string? description { get; set; }

        public bool can_execute { get; set; }

        public string? definition { get; set; }

        public string volatility { get; set; } = string.Empty;

        public int input_count { get; set; }

        public int default_count { get; set; }
    }

    private sealed class ArgumentRow
    {
        public long oid { get; set; }

        public int ordinal { get; set; }

        public string name { get; set; } = string.Empty;

        public string data_type { get; set; } = string.Empty;

        public string mode { get; set; } = "i";
    }
#pragma warning restore IDE1006, CA1707
}
