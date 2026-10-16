using System.Collections.Concurrent;
using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Database;
using DatabaseAIAgent.Database.Providers;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Api.Tests.Databases;

/// <summary>
/// Stands in for a live PostgreSQL server: parsing, connection-string building and the setup script come from the real
/// provider; only the network round trip is simulated. Password "wrong-password" fails authentication.
/// </summary>
public sealed class FakePostgresProvider : ICustomerDatabaseProvider
{
    public const string WrongPassword = "wrong-password";

    private readonly PostgresDatabaseProvider _real = new(Options.Create(new DatabaseConnectionOptions()), NullLogger<PostgresDatabaseProvider>.Instance);

    public ConcurrentQueue<ResolvedConnection> Tested { get; } = new();

    public DatabaseProviderKind Kind => _real.Kind;

    public string DisplayName => _real.DisplayName;

    public int DefaultPort => _real.DefaultPort;

    public string LeastPrivilegeSetupScript => _real.LeastPrivilegeSetupScript;

    public DbConnection CreateConnection(ResolvedConnection connection, int connectTimeoutSeconds) => _real.CreateConnection(connection, connectTimeoutSeconds);

    public ParsedConnectionString ParseConnectionString(string connectionString) => _real.ParseConnectionString(connectionString);

    public Task<ConnectionTestResult> TestAsync(ResolvedConnection connection, CancellationToken cancellationToken)
    {
        Tested.Enqueue(connection);
        return Task.FromResult(connection.Password == WrongPassword
            ? ConnectionTestResult.Failed(ConnectionFailureKind.AuthenticationFailed, TimeSpan.FromMilliseconds(5))
            : new ConnectionTestResult(ConnectionFailureKind.None, "PostgreSQL 17.2", new PermissionSummary(false, false, false, 3), TimeSpan.FromMilliseconds(5)));
    }
}
