using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Database.Networking;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Database.Connections;

/// <summary>A customer database could not be opened. <see cref="Failure"/> is a safe category for users and logs.</summary>
public sealed class CustomerDatabaseUnavailableException(ConnectionFailureKind failure)
    : Exception($"The customer database is unavailable ({failure}).")
{
    public CustomerDatabaseUnavailableException()
        : this(ConnectionFailureKind.Unknown)
    {
    }

    public CustomerDatabaseUnavailableException(string message)
        : this(ConnectionFailureKind.Unknown)
    {
        _ = message;
    }

    public CustomerDatabaseUnavailableException(string message, Exception innerException)
        : this(ConnectionFailureKind.Unknown)
    {
        _ = (message, innerException);
    }

    public ConnectionFailureKind Failure { get; } = failure;
}

internal sealed class CustomerDatabaseConnector(
    IEnumerable<ICustomerDatabaseProvider> providers,
    IDatabaseConnectionRepository connections,
    ConnectionCredentials credentials,
    HostAddressGuard hostGuard,
    IOptions<DatabaseConnectionOptions> options) : ICustomerDatabaseConnector
{
    public async Task<DbConnection> OpenAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await connections.FindAsync(connectionId, cancellationToken)
            ?? throw new CustomerDatabaseUnavailableException(ConnectionFailureKind.Unknown);

        // DNS may have changed since the connection was saved; re-check before every open.
        if (!(await hostGuard.CheckAsync(connection.Host, cancellationToken)).Allowed)
        {
            throw new CustomerDatabaseUnavailableException(ConnectionFailureKind.HostNotAllowed);
        }

        var (username, password) = credentials.Unprotect(connection.Id, connection.EncryptedCredentials);
        var provider = providers.Single(p => p.Kind == connection.Provider);
        var db = provider.CreateConnection(
            new ResolvedConnection(connection.Provider, connection.Host, connection.Port, connection.DatabaseName, username, password, connection.TlsMode),
            options.Value.ConnectTimeoutSeconds);

        try
        {
            await db.OpenAsync(cancellationToken);
            return db;
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
        {
            await db.DisposeAsync();
            throw new CustomerDatabaseUnavailableException(ConnectionFailureKind.HostUnreachable);
        }
    }
}
