using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Domain.Databases;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace DatabaseAIAgent.Database.Providers;

internal sealed class MySqlDatabaseProvider(IOptions<DatabaseConnectionOptions> options, ILogger<MySqlDatabaseProvider> logger)
    : CustomerDatabaseProviderBase(options, logger)
{
    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Server", "Host", "DataSource", "Address", "Addr", "NetworkAddress", "Port", "Database", "InitialCatalog", "UserID", "User",
        "Username", "Uid", "UserName", "Password", "Pwd", "SslMode", "SSLMode",
    };

    public override DatabaseProviderKind Kind => DatabaseProviderKind.MySql;

    public override string DisplayName => "MySQL";

    public override int DefaultPort => 3306;

    public override string LeastPrivilegeSetupScript => """
        -- Run as an administrator. Creates an account that can only EXECUTE the procedures you approve.
        CREATE USER '{user}'@'%' IDENTIFIED BY '{password}';
        -- Grant EXECUTE only on procedures you intend to expose, for example:
        -- GRANT EXECUTE ON PROCEDURE `{database}`.GetOutstandingInvoices TO '{user}'@'%';
        -- Define reporting procedures with SQL SECURITY DEFINER so the account needs no table privileges.
        -- Do NOT grant SELECT, INSERT, UPDATE, DELETE or ALL PRIVILEGES.
        -- Restrict '%' to the platform's egress IP address where possible, e.g. '{user}'@'203.0.113.10'.
        """;

    public override DbConnection CreateConnection(ResolvedConnection connection, int connectTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var builder = new MySqlConnectionStringBuilder
        {
            Server = connection.Host,
            Port = (uint)connection.Port,
            Database = connection.Database,
            UserID = connection.Username,
            Password = connection.Password,
            SslMode = connection.TlsMode switch
            {
                TlsMode.Disabled => MySqlSslMode.None,
                TlsMode.Preferred => MySqlSslMode.Preferred,
                TlsMode.Required => MySqlSslMode.Required,
                _ => MySqlSslMode.VerifyFull,
            },
            ConnectionTimeout = (uint)connectTimeoutSeconds,
            DefaultCommandTimeout = (uint)Settings.TestCommandTimeoutSeconds,
            ApplicationName = ApplicationName,
            PersistSecurityInfo = false,
            // Never let the server read files from the platform host (LOAD DATA LOCAL INFILE).
            AllowLoadLocalInfile = false,
            AllowUserVariables = false,
            AllowPublicKeyRetrieval = false,
            MaximumPoolSize = 10,
        };
        return new MySqlConnection(builder.ConnectionString);
    }

    public override ParsedConnectionString ParseConnectionString(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (builder.Server?.Contains(',', StringComparison.Ordinal) == true)
        {
            throw new ArgumentException("Multiple hosts are not supported; enter a single host.");
        }

        return new ParsedConnectionString(
            string.IsNullOrWhiteSpace(builder.Server) ? null : builder.Server,
            connectionString.Contains("port", StringComparison.OrdinalIgnoreCase) ? (int)builder.Port : null,
            string.IsNullOrWhiteSpace(builder.Database) ? null : builder.Database,
            string.IsNullOrWhiteSpace(builder.UserID) ? null : builder.UserID,
            string.IsNullOrWhiteSpace(builder.Password) ? null : builder.Password,
            builder.SslMode switch
            {
                MySqlSslMode.None => TlsMode.Disabled,
                MySqlSslMode.Preferred => TlsMode.Preferred,
                MySqlSslMode.Required or MySqlSslMode.VerifyCA => TlsMode.Required,
                _ => TlsMode.VerifyFull,
            },
            UnsupportedKeys(connectionString, SupportedKeys));
    }

    protected override async Task<string?> ReadServerVersionAsync(DbConnection connection, CancellationToken cancellationToken) =>
        "MySQL " + await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT VERSION()", cancellationToken: cancellationToken));

    protected override async Task<PermissionSummary> InspectPermissionsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES
                        WHERE GRANTEE = @grantee AND PRIVILEGE_TYPE IN ('SUPER', 'CREATE USER', 'GRANT OPTION', 'SYSTEM_USER')) AS IsAdministrator,
                (EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = @grantee AND PRIVILEGE_TYPE = 'SELECT')
                 OR EXISTS (SELECT 1 FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE = @grantee AND TABLE_SCHEMA = DATABASE() AND PRIVILEGE_TYPE = 'SELECT')
                 OR EXISTS (SELECT 1 FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE = @grantee AND TABLE_SCHEMA = DATABASE() AND PRIVILEGE_TYPE = 'SELECT')) AS CanReadTablesDirectly,
                (EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = @grantee AND PRIVILEGE_TYPE IN ('INSERT', 'UPDATE', 'DELETE'))
                 OR EXISTS (SELECT 1 FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE = @grantee AND TABLE_SCHEMA = DATABASE() AND PRIVILEGE_TYPE IN ('INSERT', 'UPDATE', 'DELETE'))
                 OR EXISTS (SELECT 1 FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE = @grantee AND TABLE_SCHEMA = DATABASE() AND PRIVILEGE_TYPE IN ('INSERT', 'UPDATE', 'DELETE'))) AS CanWriteData,
                (SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE() AND ROUTINE_TYPE = 'PROCEDURE') AS ExecutableProcedureCount
            """;
        var currentUser = await connection.ExecuteScalarAsync<string>(new CommandDefinition("SELECT CURRENT_USER()", cancellationToken: cancellationToken)) ?? string.Empty;
        var at = currentUser.LastIndexOf('@');
        var grantee = at > 0 ? $"'{currentUser[..at]}'@'{currentUser[(at + 1)..]}'" : $"'{currentUser}'@'%'";

        var row = await connection.QuerySingleAsync<PermissionRow>(new CommandDefinition(sql, new { grantee }, cancellationToken: cancellationToken));
        return new PermissionSummary(row.IsAdministrator != 0, row.CanReadTablesDirectly != 0, row.CanWriteData != 0, (int)row.ExecutableProcedureCount);
    }

    // Server error numbers: https://dev.mysql.com/doc/mysql-errors/8.0/en/server-error-reference.html
    protected override ConnectionFailureKind ClassifyProviderError(Exception exception) => exception is MySqlException mysql
        ? mysql.ErrorCode switch
        {
            MySqlErrorCode.UnableToConnectToHost => ConnectionFailureKind.HostUnreachable,
            MySqlErrorCode.CommandTimeoutExpired => ConnectionFailureKind.Timeout,
            _ => mysql.Number switch
            {
                1045 => ConnectionFailureKind.AuthenticationFailed, // ER_ACCESS_DENIED_ERROR
                1049 => ConnectionFailureKind.DatabaseNotFound, // ER_BAD_DB_ERROR
                1044 or 1142 or 1370 => ConnectionFailureKind.PermissionDenied, // DB / table / routine access denied
                _ => ConnectionFailureKind.Unknown,
            },
        }
        : ConnectionFailureKind.Unknown;

    private sealed class PermissionRow
    {
        public long IsAdministrator { get; set; }

        public long CanReadTablesDirectly { get; set; }

        public long CanWriteData { get; set; }

        public long ExecutableProcedureCount { get; set; }
    }
}
