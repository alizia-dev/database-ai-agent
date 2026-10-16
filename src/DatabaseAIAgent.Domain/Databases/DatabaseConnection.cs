using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Databases;

public enum DatabaseProviderKind
{
    SqlServer = 0,
    PostgreSql = 1,
    MySql = 2,
}

/// <summary>Provider-neutral transport security; each provider maps it to its own connection-string settings.</summary>
public enum TlsMode
{
    /// <summary>No encryption. Only acceptable on a trusted private network.</summary>
    Disabled = 0,

    /// <summary>Encrypt if the server supports it; no certificate validation.</summary>
    Preferred = 1,

    /// <summary>Always encrypt; the server certificate is not validated (self-signed certificates work).</summary>
    Required = 2,

    /// <summary>Always encrypt and validate the certificate chain and host name (recommended).</summary>
    VerifyFull = 3,
}

public enum ConnectionStatus
{
    Untested = 0,
    Connected = 1,
    Failed = 2,
}

/// <summary>Why a connection attempt failed, as a safe category (never the raw driver message).</summary>
public enum ConnectionFailureKind
{
    None = 0,
    HostNotAllowed = 1,
    HostUnreachable = 2,
    AuthenticationFailed = 3,
    DatabaseNotFound = 4,
    Timeout = 5,
    TlsFailure = 6,
    PermissionDenied = 7,
    Unknown = 8,
}

/// <summary>What the connecting account may do, used to recommend least privilege.</summary>
public sealed record PermissionSummary(
    bool IsAdministrator,
    bool CanReadTablesDirectly,
    bool CanWriteData,
    int ExecutableProcedureCount)
{
    public static PermissionSummary Unknown { get; } = new(false, false, false, 0);
}

/// <summary>
/// A connection to a customer database. Only metadata is stored in clear text; the username and password are a single
/// encrypted blob bound to this record. Customer data is never copied into the platform.
/// </summary>
public sealed class DatabaseConnection : AuditableEntity
{
    public const int NameMaxLength = 100;
    public const int HostMaxLength = 253;
    public const int DatabaseNameMaxLength = 128;
    public const int ServerVersionMaxLength = 200;

    private DatabaseConnection()
    {
    }

    public DatabaseConnection(string name, DatabaseProviderKind provider, string host, int port, string databaseName, TlsMode tlsMode, Guid createdByUserId)
    {
        Rename(name);
        UpdateEndpoint(provider, host, port, databaseName, tlsMode);
        CreatedByUserId = createdByUserId;
    }

    public string Name { get; private set; } = string.Empty;

    public DatabaseProviderKind Provider { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public string DatabaseName { get; private set; } = string.Empty;

    public TlsMode TlsMode { get; private set; }

    /// <summary>Encrypted credentials (opaque to the domain). Never returned by any API.</summary>
    public string EncryptedCredentials { get; private set; } = string.Empty;

    public ConnectionStatus Status { get; private set; }

    public DateTimeOffset? LastTestedAt { get; private set; }

    public ConnectionFailureKind LastFailure { get; private set; }

    public string? ServerVersion { get; private set; }

    public PermissionSummary Permissions { get; private set; } = PermissionSummary.Unknown;

    public Guid CreatedByUserId { get; private set; }

    public void Rename(string name) => Name = Text.Required(name, nameof(Name), NameMaxLength);

    public void UpdateEndpoint(DatabaseProviderKind provider, string host, int port, string databaseName, TlsMode tlsMode)
    {
        if (!Enum.IsDefined(provider) || !Enum.IsDefined(tlsMode))
        {
            throw new DomainException("Unsupported database provider or TLS mode.");
        }

        if (port is < 1 or > 65535)
        {
            throw new DomainException("Port must be between 1 and 65535.");
        }

        Provider = provider;
        Host = Text.Required(host, nameof(Host), HostMaxLength);
        Port = port;
        DatabaseName = Text.Required(databaseName, nameof(DatabaseName), DatabaseNameMaxLength);
        TlsMode = tlsMode;
        Status = ConnectionStatus.Untested;
    }

    public void SetEncryptedCredentials(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            throw new DomainException("Credentials are required.");
        }

        EncryptedCredentials = encrypted;
        Status = ConnectionStatus.Untested;
    }

    public void RecordTest(DateTimeOffset at, ConnectionFailureKind failure, string? serverVersion, PermissionSummary? permissions)
    {
        LastTestedAt = at;
        LastFailure = failure;
        Status = failure == ConnectionFailureKind.None ? ConnectionStatus.Connected : ConnectionStatus.Failed;
        if (failure == ConnectionFailureKind.None)
        {
            ServerVersion = serverVersion is { Length: > ServerVersionMaxLength } ? serverVersion[..ServerVersionMaxLength] : serverVersion;
            Permissions = permissions ?? PermissionSummary.Unknown;
        }
    }
}
