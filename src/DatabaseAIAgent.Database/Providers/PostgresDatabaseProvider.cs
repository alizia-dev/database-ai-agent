using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DatabaseAIAgent.Database.Providers;

internal sealed class PostgresDatabaseProvider(IOptions<DatabaseConnectionOptions> options, ILogger<PostgresDatabaseProvider> logger)
    : CustomerDatabaseProviderBase(options, logger)
{
    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Server", "Port", "Database", "DB", "Username", "UserName", "UserID", "User", "UID", "Password", "PSW", "PWD", "SslMode", "SSLMode",
    };

    public override DatabaseProviderKind Kind => DatabaseProviderKind.PostgreSql;

    public override string DisplayName => "PostgreSQL";

    public override int DefaultPort => 5432;

    public override string LeastPrivilegeSetupScript => """
        -- Run as a superuser or the database owner. Creates a role that can only EXECUTE the functions/procedures you approve.
        CREATE ROLE "{user}" WITH LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE;
        GRANT CONNECT ON DATABASE "{database}" TO "{user}";
        \c "{database}"
        -- PostgreSQL grants EXECUTE on new functions to PUBLIC by default; revoke it so only approved routines run.
        ALTER DEFAULT PRIVILEGES REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
        REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA public FROM PUBLIC;
        -- Allow resolving objects in the schema that holds your reporting routines:
        GRANT USAGE ON SCHEMA reporting TO "{user}";
        -- Grant EXECUTE only on routines you intend to expose, for example:
        -- GRANT EXECUTE ON FUNCTION reporting.get_outstanding_invoices(numeric) TO "{user}";
        -- Do NOT grant SELECT/INSERT/UPDATE/DELETE on tables. Mark reporting functions SECURITY DEFINER instead.
        """;

    public override DbConnection CreateConnection(ResolvedConnection connection, int connectTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Database = connection.Database,
            Username = connection.Username,
            Password = connection.Password,
            SslMode = connection.TlsMode switch
            {
                TlsMode.Disabled => SslMode.Disable,
                TlsMode.Preferred => SslMode.Prefer,
                TlsMode.Required => SslMode.Require,
                _ => SslMode.VerifyFull,
            },
            Timeout = connectTimeoutSeconds,
            CommandTimeout = Settings.TestCommandTimeoutSeconds,
            ApplicationName = ApplicationName,
            PersistSecurityInfo = false,
            IncludeErrorDetail = false,
            MaxPoolSize = 10,
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    public override ParsedConnectionString ParseConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (builder.Host?.Contains(',', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Multiple hosts are not supported; enter a single host.");
        }

        return new ParsedConnectionString(
            string.IsNullOrWhiteSpace(builder.Host) ? null : builder.Host,
            connectionString.Contains("port", StringComparison.OrdinalIgnoreCase) ? builder.Port : null,
            string.IsNullOrWhiteSpace(builder.Database) ? null : builder.Database,
            string.IsNullOrWhiteSpace(builder.Username) ? null : builder.Username,
            string.IsNullOrWhiteSpace(builder.Password) ? null : builder.Password,
            builder.SslMode switch
            {
                SslMode.Disable => TlsMode.Disabled,
                SslMode.Allow or SslMode.Prefer => TlsMode.Preferred,
                SslMode.Require or SslMode.VerifyCA => TlsMode.Required,
                _ => TlsMode.VerifyFull,
            },
            UnsupportedKeys(connectionString, SupportedKeys));
    }

    protected override async Task<string?> ReadServerVersionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT 'PostgreSQL ' || current_setting('server_version')", cancellationToken: cancellationToken));

    protected override async Task<PermissionSummary> InspectPermissionsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                (r.rolsuper OR EXISTS (SELECT 1 FROM pg_database d WHERE d.datname = current_database() AND d.datdba = r.oid)) AS is_administrator,
                EXISTS (
                    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind IN ('r', 'p', 'v', 'm') AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND n.nspname NOT LIKE 'pg_toast%' AND has_table_privilege(c.oid, 'SELECT')) AS can_read_tables_directly,
                EXISTS (
                    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                      AND (has_table_privilege(c.oid, 'INSERT') OR has_table_privilege(c.oid, 'UPDATE') OR has_table_privilege(c.oid, 'DELETE'))) AS can_write_data,
                (SELECT COUNT(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                 WHERE n.nspname NOT IN ('pg_catalog', 'information_schema') AND p.prokind IN ('f', 'p')
                   AND has_function_privilege(p.oid, 'EXECUTE'))::int AS executable_procedure_count
            FROM pg_roles r
            WHERE r.rolname = current_user
            """;
        var row = await connection.QuerySingleAsync<PermissionRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return new PermissionSummary(row.is_administrator, row.can_read_tables_directly, row.can_write_data, row.executable_procedure_count);
    }

    protected override ConnectionFailureKind ClassifyProviderError(Exception exception) => exception switch
    {
        PostgresException pg => pg.SqlState switch
        {
            "28P01" or "28000" => ConnectionFailureKind.AuthenticationFailed,
            "3D000" => ConnectionFailureKind.DatabaseNotFound,
            "42501" => ConnectionFailureKind.PermissionDenied,
            _ => ConnectionFailureKind.Unknown,
        },
        NpgsqlException { InnerException: TimeoutException } => ConnectionFailureKind.Timeout,
        _ => ConnectionFailureKind.Unknown,
    };

#pragma warning disable IDE1006, CA1707 // Column names as returned by PostgreSQL.
    private sealed class PermissionRow
    {
        public bool is_administrator { get; set; }

        public bool can_read_tables_directly { get; set; }

        public bool can_write_data { get; set; }

        public int executable_procedure_count { get; set; }
    }
#pragma warning restore IDE1006, CA1707
}
