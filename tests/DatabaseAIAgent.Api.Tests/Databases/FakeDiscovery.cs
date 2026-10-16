using System.Collections.Concurrent;
using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Data.Sqlite;

namespace DatabaseAIAgent.Api.Tests.Databases;

/// <summary>A connection object that carries which customer connection it stands for (no real network traffic).</summary>
public sealed class TaggedConnection(Guid connectionId) : SqliteConnection("DataSource=:memory:")
{
    public Guid ConnectionId { get; } = connectionId;
}

/// <summary>
/// Replaces the network edge of schema analysis: the connector returns a tagged in-memory connection (or fails, if a
/// failure is configured for that connection) and the discoverer returns the catalog configured for it. Everything
/// else (queue, job handler, catalog merge, API) is the real code.
/// </summary>
public sealed class FakeDiscovery : ICustomerDatabaseConnector, ISchemaDiscoverer
{
    public ConcurrentDictionary<Guid, DiscoveredDatabase> Catalogs { get; } = new();

    public ConcurrentDictionary<Guid, ConnectionFailureKind> Failures { get; } = new();

    public DatabaseProviderKind Kind => DatabaseProviderKind.PostgreSql;

    public Task<DbConnection> OpenAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Failures.TryGetValue(connectionId, out var failure)
            ? throw new CustomerDatabaseUnavailableException(failure)
            : Task.FromResult<DbConnection>(new TaggedConnection(connectionId));

    public Task<DiscoveredDatabase> DiscoverAsync(DbConnection connection, DiscoveryLimits limits, CancellationToken cancellationToken) =>
        Task.FromResult(Catalogs[((TaggedConnection)connection).ConnectionId]);

    public static DiscoveredDatabase Crm(params string[] procedures) => new(
        ["public", "reporting"],
        [
            new DiscoveredTable("public", "customers", TableKind.Table, "Customer master data", 1200,
                [
                    new DiscoveredColumn("id", 1, "integer", null, null, null, false, true, true, null),
                    new DiscoveredColumn("name", 2, "character varying(100)", 100, null, null, false, false, false, "Legal name"),
                    new DiscoveredColumn("balance", 3, "numeric(12,2)", null, 12, 2, true, false, false, null),
                ],
                [new DiscoveredIndex("customers_pkey", true, true, ["id"])]),
            new DiscoveredTable("public", "invoices", TableKind.Table, null, 5400,
                [
                    new DiscoveredColumn("id", 1, "integer", null, null, null, false, true, true, null),
                    new DiscoveredColumn("customer_id", 2, "integer", null, null, null, false, false, false, null),
                ],
                [new DiscoveredIndex("invoices_pkey", true, true, ["id"])]),
            new DiscoveredTable("reporting", "open_invoices", TableKind.View, null, null,
                [new DiscoveredColumn("invoice_id", 1, "integer", null, null, null, true, false, false, null)], []),
        ],
        [new DiscoveredRelationship("invoices_customer_fk", "public", "invoices", ["customer_id"], "public", "customers", ["id"])],
        procedures.Select(name => new DiscoveredRoutine(
            "reporting", name, "p_min_balance numeric", RoutineKind.TableFunction, $"{name} report", "record", true,
            name.StartsWith("set_", StringComparison.Ordinal)
                ? "BEGIN UPDATE customers SET balance = 0; END"
                : "SELECT c.id, c.name, c.balance FROM customers c WHERE c.balance > p_min_balance",
            name.StartsWith("set_", StringComparison.Ordinal) ? "VOLATILE" : "STABLE",
            12.5,
            [new DiscoveredParameter("p_min_balance", 1, "numeric", null, null, null, ParameterDirection.In, true)],
            [new DiscoveredResultColumn("id", 1, "integer", true), new DiscoveredResultColumn("name", 2, "text", true)])).ToList(),
        ["Example warning from discovery."]);
}
