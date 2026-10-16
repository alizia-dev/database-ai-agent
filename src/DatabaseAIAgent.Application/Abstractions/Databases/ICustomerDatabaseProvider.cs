using System.Data.Common;
using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Application.Abstractions.Databases;

/// <summary>Everything needed to open a connection, with credentials already decrypted. Never logged or serialized.</summary>
public sealed record ResolvedConnection(
    DatabaseProviderKind Provider,
    string Host,
    int Port,
    string Database,
    string Username,
    string Password,
    TlsMode TlsMode)
{
    // Keep credentials out of logs and debugger displays if this record is ever formatted.
    public override string ToString() => $"{Provider} {Host}:{Port}/{Database}";
}

public sealed record ConnectionTestResult(
    ConnectionFailureKind Failure,
    string? ServerVersion,
    PermissionSummary? Permissions,
    TimeSpan Duration)
{
    public bool Succeeded => Failure == ConnectionFailureKind.None;

    public static ConnectionTestResult Failed(ConnectionFailureKind failure, TimeSpan duration) => new(failure, null, null, duration);
}

/// <summary>Fields extracted from a pasted connection string. Unknown or unsafe options are dropped, never stored.</summary>
public sealed record ParsedConnectionString(
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? Password,
    TlsMode? TlsMode,
    IReadOnlyList<string> IgnoredOptions);

/// <summary>
/// A customer database engine (SQL Server, PostgreSQL, MySQL). Connection strings are always built with the driver's
/// builder from validated fields plus a fixed set of safe options; arbitrary user-supplied options are never passed.
/// </summary>
public interface ICustomerDatabaseProvider
{
    DatabaseProviderKind Kind { get; }

    string DisplayName { get; }

    int DefaultPort { get; }

    DbConnection CreateConnection(ResolvedConnection connection, int connectTimeoutSeconds);

    /// <summary>Connects, reads the server version and inspects the account's privileges.</summary>
    Task<ConnectionTestResult> TestAsync(ResolvedConnection connection, CancellationToken cancellationToken);

    /// <exception cref="ArgumentException">The text is not a valid connection string for this provider.</exception>
    ParsedConnectionString ParseConnectionString(string connectionString);

    /// <summary>SQL to create a dedicated least-privilege account (placeholders: {user}, {password}, {database}).</summary>
    string LeastPrivilegeSetupScript { get; }
}

/// <summary>
/// Opens connections to configured customer databases for discovery and tool execution (later phases). Decrypts
/// credentials and re-validates the target host on every open.
/// </summary>
public interface ICustomerDatabaseConnector
{
    Task<DbConnection> OpenAsync(Guid connectionId, CancellationToken cancellationToken);
}
