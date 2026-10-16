using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Database.Connections;

/// <summary>
/// Connection details entered by an administrator, either as fields or as a pasted connection string (parsed into the
/// same fields; unsupported options are dropped). On update, omit <see cref="Password"/> to keep the stored credentials.
/// </summary>
public sealed record ConnectionInput(
    string Name,
    DatabaseProviderKind Provider,
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? Password,
    TlsMode? TlsMode,
    string? ConnectionString);

public sealed record ConnectionTestDto(
    bool Succeeded,
    ConnectionFailureKind Failure,
    string Message,
    string? ServerVersion,
    PermissionSummary? Permissions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> IgnoredOptions,
    long DurationMs);

/// <summary>A configured connection. Credentials are never included, not even the username.</summary>
public sealed record DatabaseConnectionDto(
    Guid Id,
    string Name,
    DatabaseProviderKind Provider,
    string Host,
    int Port,
    string DatabaseName,
    TlsMode TlsMode,
    ConnectionStatus Status,
    DateTimeOffset? LastTestedAt,
    ConnectionFailureKind LastFailure,
    string? LastFailureMessage,
    string? ServerVersion,
    PermissionSummary Permissions,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAt);

public sealed record SavedConnectionDto(DatabaseConnectionDto Connection, ConnectionTestDto Test);

public sealed record DatabaseProviderDto(DatabaseProviderKind Kind, string DisplayName, int DefaultPort, string LeastPrivilegeSetupScript);

internal static class ConnectionMessages
{
    public static string For(ConnectionFailureKind failure) => failure switch
    {
        ConnectionFailureKind.None => "Connected successfully.",
        ConnectionFailureKind.HostNotAllowed => "This host is not allowed for this deployment.",
        ConnectionFailureKind.HostUnreachable => "The database server could not be reached. Check the host, port and firewall rules (allow the platform's outbound IP address).",
        ConnectionFailureKind.AuthenticationFailed => "The server rejected the username or password.",
        ConnectionFailureKind.DatabaseNotFound => "The database was not found, or this account cannot open it.",
        ConnectionFailureKind.Timeout => "The connection timed out. Check that the server is running and reachable from the platform.",
        ConnectionFailureKind.TlsFailure => "A secure (TLS) connection could not be established. Check the server certificate or choose a different TLS mode.",
        ConnectionFailureKind.PermissionDenied => "The account is not permitted to connect to this database.",
        _ => "The connection failed. Check the details and try again.",
    };

    public static IReadOnlyList<string> Warnings(TlsMode tlsMode, PermissionSummary? permissions)
    {
        var warnings = new List<string>();
        switch (tlsMode)
        {
            case TlsMode.Disabled:
                warnings.Add("Traffic is not encrypted. Use this only on a trusted private network.");
                break;
            case TlsMode.Preferred or TlsMode.Required:
                warnings.Add("The server certificate is not validated. Prefer 'Verify full' with a trusted certificate.");
                break;
        }

        if (permissions is null || permissions == PermissionSummary.Unknown)
        {
            return warnings;
        }

        if (permissions.IsAdministrator)
        {
            warnings.Add("This account has administrator rights. Use a dedicated account that can only EXECUTE approved stored procedures.");
        }

        if (permissions.CanWriteData)
        {
            warnings.Add("This account can change table data directly. A read-only account with EXECUTE on approved procedures is recommended.");
        }

        if (permissions.CanReadTablesDirectly)
        {
            warnings.Add("This account can read tables directly. Least privilege: grant EXECUTE on approved procedures instead of SELECT on tables.");
        }

        if (permissions.ExecutableProcedureCount == 0)
        {
            warnings.Add("This account cannot execute any stored procedures yet. Grant EXECUTE on the procedures the assistant should use.");
        }

        return warnings;
    }
}
