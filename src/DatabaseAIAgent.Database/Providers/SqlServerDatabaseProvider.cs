using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Database.Providers;

internal sealed class SqlServerDatabaseProvider(IOptions<DatabaseConnectionOptions> options, ILogger<SqlServerDatabaseProvider> logger)
    : CustomerDatabaseProviderBase(options, logger)
{
    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Server", "DataSource", "Address", "Addr", "NetworkAddress", "Database", "InitialCatalog", "UserID", "UID", "User",
        "Password", "PWD", "Encrypt", "TrustServerCertificate",
    };

    public override DatabaseProviderKind Kind => DatabaseProviderKind.SqlServer;

    public override string DisplayName => "Microsoft SQL Server";

    public override int DefaultPort => 1433;

    public override string LeastPrivilegeSetupScript => """
        -- Run as an administrator. Creates a login that can only EXECUTE the procedures you approve.
        CREATE LOGIN [{user}] WITH PASSWORD = '{password}', CHECK_POLICY = ON;
        USE [{database}];
        CREATE USER [{user}] FOR LOGIN [{user}];
        -- Lets the platform read procedure and parameter metadata without reading table data.
        GRANT VIEW DEFINITION TO [{user}];
        -- Grant EXECUTE only on procedures you intend to expose, for example:
        -- GRANT EXECUTE ON OBJECT::dbo.GetOutstandingInvoices TO [{user}];
        -- Or on a dedicated schema that contains only reporting procedures:
        -- GRANT EXECUTE ON SCHEMA::reporting TO [{user}];
        -- Do NOT add this user to db_owner, db_datareader or db_datawriter.
        """;

    public override DbConnection CreateConnection(ResolvedConnection connection, int connectTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{connection.Host},{connection.Port}",
            InitialCatalog = connection.Database,
            UserID = connection.Username,
            Password = connection.Password,
            IntegratedSecurity = false,
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
            ApplicationName = ApplicationName,
            ConnectTimeout = connectTimeoutSeconds,
            CommandTimeout = Settings.TestCommandTimeoutSeconds,
            Encrypt = connection.TlsMode is TlsMode.Required or TlsMode.VerifyFull ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = connection.TlsMode != TlsMode.VerifyFull,
        };
        return new SqlConnection(builder.ConnectionString);
    }

    public override ParsedConnectionString ParseConnectionString(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var (host, port) = SplitDataSource(builder.DataSource);
        var tls = builder.Encrypt == SqlConnectionEncryptOption.Optional
            ? TlsMode.Preferred
            : builder.TrustServerCertificate ? TlsMode.Required : TlsMode.VerifyFull;
        return new ParsedConnectionString(
            host,
            port,
            NullIfEmpty(builder.InitialCatalog),
            NullIfEmpty(builder.UserID),
            NullIfEmpty(builder.Password),
            tls,
            UnsupportedKeys(connectionString, SupportedKeys));
    }

    protected override async Task<string?> ReadServerVersionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        FirstLine(await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT @@VERSION", cancellationToken: cancellationToken)));

    protected override async Task<PermissionSummary> InspectPermissionsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CAST(ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) | ISNULL(IS_ROLEMEMBER('db_owner'), 0) AS bit) AS IsAdministrator,
                CAST(CASE WHEN ISNULL(IS_ROLEMEMBER('db_datareader'), 0) = 1 OR EXISTS (
                    SELECT 1 FROM sys.tables t
                    WHERE HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', 'SELECT') = 1)
                    THEN 1 ELSE 0 END AS bit) AS CanReadTablesDirectly,
                CAST(CASE WHEN ISNULL(IS_ROLEMEMBER('db_datawriter'), 0) = 1 OR EXISTS (
                    SELECT 1 FROM sys.tables t
                    WHERE HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', 'INSERT') = 1
                       OR HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', 'UPDATE') = 1
                       OR HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name), 'OBJECT', 'DELETE') = 1)
                    THEN 1 ELSE 0 END AS bit) AS CanWriteData,
                (SELECT COUNT(*) FROM sys.procedures p
                 WHERE p.is_ms_shipped = 0
                   AND HAS_PERMS_BY_NAME(QUOTENAME(SCHEMA_NAME(p.schema_id)) + '.' + QUOTENAME(p.name), 'OBJECT', 'EXECUTE') = 1) AS ExecutableProcedureCount
            """;
        var row = await connection.QuerySingleAsync<PermissionRow>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return new PermissionSummary(row.IsAdministrator, row.CanReadTablesDirectly, row.CanWriteData, row.ExecutableProcedureCount);
    }

    protected override ConnectionFailureKind ClassifyProviderError(Exception exception) => exception is SqlException sql
        ? sql.Number switch
        {
            18456 or 18452 or 18470 or 18486 or 18487 or 18488 => ConnectionFailureKind.AuthenticationFailed,
            4060 => ConnectionFailureKind.DatabaseNotFound,
            916 or 229 or 230 or 297 => ConnectionFailureKind.PermissionDenied,
            -2 or 258 => ConnectionFailureKind.Timeout, // 258: WAIT_TIMEOUT (e.g. refused connection retried until timeout)
            -2146893019 or -2146893022 => ConnectionFailureKind.TlsFailure,
            20 or 53 or 40 or 2 or 10060 or 10061 or 11001 or 11004 or -1 => ConnectionFailureKind.HostUnreachable,
            // Severity 20+ without a more specific number is a connection-level (network) failure.
            _ when sql.Class >= 20 => ConnectionFailureKind.HostUnreachable,
            _ => ConnectionFailureKind.Unknown,
        }
        : ConnectionFailureKind.Unknown;

    private static (string? Host, int? Port) SplitDataSource(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return (null, null);
        }

        var value = dataSource.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        if (value.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Named instances (server\\instance) are not supported; use the instance's TCP port instead.");
        }

        var comma = value.LastIndexOf(',');
        return comma > 0 && int.TryParse(value[(comma + 1)..], out var port)
            ? (value[..comma].Trim(), port)
            : (value, null);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class PermissionRow
    {
        public bool IsAdministrator { get; set; }

        public bool CanReadTablesDirectly { get; set; }

        public bool CanWriteData { get; set; }

        public int ExecutableProcedureCount { get; set; }
    }
}
