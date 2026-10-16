using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Database.Discovery;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Jobs;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Infrastructure.Jobs;
using DatabaseAIAgent.Infrastructure.Persistence;
using DatabaseAIAgent.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DatabaseAIAgent.UnitTests.Infrastructure;

/// <summary>Job queue and catalog merge against a real (SQLite) platform database.</summary>
public sealed class JobsAndCatalogTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IClock _clock = Substitute.For<IClock>();
    private DateTimeOffset _now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        _clock.UtcNow.Returns(_ => _now);
        await _connection.OpenAsync();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(_clock);
        services.AddDbContext<PlatformDbContext>(o => o.UseSqlite(_connection));
        services.AddScoped<IJobQueue, DatabaseJobQueue>();
        services.AddSingleton<IJobProcessor, DatabaseJobProcessor>();
        services.AddSingleton<RecordingHandler>();
        services.AddScoped<IJobHandler>(sp => sp.GetRequiredService<RecordingHandler>());
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task EnqueuedJob_IsInvisibleUntilSaved_ThenProcessedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid jobId;
        await using (var scope = _services.CreateAsyncScope())
        {
            jobId = scope.ServiceProvider.GetRequiredService<IJobQueue>().Enqueue(RecordingHandler.Type, new { value = 42 });
            Assert.False(await Processor.ProcessNextAsync(ct)); // not saved yet
            await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync(ct);
        }

        Assert.True(await Processor.ProcessNextAsync(ct));
        Assert.False(await Processor.ProcessNextAsync(ct));

        var job = await Job(jobId);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.Single(Handler.Payloads, p => p.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailingJob_RetriesWithBackoffAndThenFails()
    {
        var ct = TestContext.Current.CancellationToken;
        Handler.FailNext = 5;
        var jobId = await EnqueueAsync(maxAttempts: 2);

        Assert.True(await Processor.ProcessNextAsync(ct));
        var afterFirst = await Job(jobId);
        Assert.Equal(JobStatus.Queued, afterFirst.Status);
        Assert.True(afterFirst.AvailableAt > _now);
        Assert.False(await Processor.ProcessNextAsync(ct)); // backoff not elapsed

        _now = _now.AddMinutes(5);
        Assert.True(await Processor.ProcessNextAsync(ct));
        var final = await Job(jobId);
        Assert.Equal(JobStatus.Failed, final.Status);
        Assert.Equal(2, final.Attempts);
        Assert.Equal(nameof(InvalidOperationException), final.LastError);
    }

    [Fact]
    public async Task ExpiredClaim_FromACrashedWorker_IsRecoveredAndRerun()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(maxAttempts: 3);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await db.BackgroundJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Running)
                .SetProperty(j => j.Attempts, 1)
                .SetProperty(j => j.LockedUntil, _now.AddMinutes(-1)), ct);
        }

        Assert.True(await Processor.ProcessNextAsync(ct));

        var job = await Job(jobId);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task CatalogMerge_KeepsProcedureIdsStableAndFlagsRemovedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = await SeedConnectionAsync();
        var first = Discovery(["GetCustomers", "GetInvoices"], ["Customers", "Invoices"]);

        Guid getCustomersId;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var counts = await new SchemaCatalogWriter(new SchemaMetadataRepository(db)).ApplyAsync(connection, first, _now, ct);
            await db.SaveChangesAsync(ct);
            Assert.Equal((2, 2, 2, 1), (counts.Tables, counts.Procedures, counts.NewProcedures, counts.Relationships));
            getCustomersId = (await db.StoredProcedures.SingleAsync(p => p.Name == "GetCustomers", ct)).Id;
        }

        _now = _now.AddDays(1);
        var second = Discovery(["GetCustomers", "GetTopCustomers"], ["Customers"]);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var counts = await new SchemaCatalogWriter(new SchemaMetadataRepository(db)).ApplyAsync(connection, second, _now, ct);
            await db.SaveChangesAsync(ct);
            Assert.Equal((1, 1), (counts.NewProcedures, counts.MissingProcedures));
        }

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var procedures = await db.StoredProcedures.ToListAsync(ct);
            Assert.Equal(getCustomersId, procedures.Single(p => p.Name == "GetCustomers").Id);
            Assert.True(procedures.Single(p => p.Name == "GetInvoices").IsMissing);
            Assert.False(procedures.Single(p => p.Name == "GetCustomers").IsMissing);
            Assert.Equal(_now, procedures.Single(p => p.Name == "GetCustomers").LastSeenAt);

            var tables = await db.DatabaseTables.ToListAsync(ct);
            var customers = Assert.Single(tables);
            Assert.Equal(["Id", "Name"], customers.Columns.Select(c => c.Name));
            Assert.True(customers.Columns.Single(c => c.Name == "Id").IsPrimaryKey);
            Assert.Equal(["Id"], customers.Indexes.Single().Columns);

            var getCustomers = procedures.Single(p => p.Name == "GetCustomers");
            Assert.True(getCustomers.Behavior.IsReadOnly);
            Assert.Equal("CustomerId", getCustomers.Parameters.Single().Name);
            Assert.Equal(2, getCustomers.ResultColumns.Count);
        }
    }

    private IJobProcessor Processor => _services.GetRequiredService<IJobProcessor>();

    private RecordingHandler Handler => _services.GetRequiredService<RecordingHandler>();

    private async Task<Guid> EnqueueAsync(int maxAttempts)
    {
        await using var scope = _services.CreateAsyncScope();
        var id = scope.ServiceProvider.GetRequiredService<IJobQueue>().Enqueue(RecordingHandler.Type, new { value = 1 }, maxAttempts);
        await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task<BackgroundJob> Job(Guid id)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().BackgroundJobs.AsNoTracking().SingleAsync(j => j.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<DatabaseConnection> SeedConnectionAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = new User { Email = "o@example.test", UserName = "o@example.test", NormalizedEmail = "O@EXAMPLE.TEST", DisplayName = "O" };
        db.Users.Add(user);
        var connection = new DatabaseConnection("CRM", DatabaseProviderKind.SqlServer, "db.example.com", 1433, "crm", TlsMode.VerifyFull, user.Id);
        connection.SetEncryptedCredentials("v1.x.y");
        db.DatabaseConnections.Add(connection);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static DiscoveredDatabase Discovery(string[] procedures, string[] tables) => new(
        ["dbo"],
        tables.Select(t => new DiscoveredTable("dbo", t, TableKind.Table, null, 10,
            [new DiscoveredColumn("Id", 1, "int", null, null, null, false, true, true, null), new DiscoveredColumn("Name", 2, "nvarchar", 100, null, null, true, false, false, null)],
            [new DiscoveredIndex($"PK_{t}", true, true, ["Id"])])).ToList(),
        tables.Contains("Invoices") ? [new DiscoveredRelationship("FK_Invoices_Customers", "dbo", "Invoices", ["CustomerId"], "dbo", "Customers", ["Id"])] : [],
        procedures.Select(p => new DiscoveredRoutine("dbo", p, string.Empty, RoutineKind.Procedure, null, null, true,
            $"CREATE PROCEDURE dbo.{p} @CustomerId int AS SELECT Id, Name FROM dbo.Customers WHERE Id = @CustomerId", null, null,
            [new DiscoveredParameter("CustomerId", 1, "int", null, null, null, ParameterDirection.In, false)],
            [new DiscoveredResultColumn("Id", 1, "int", false), new DiscoveredResultColumn("Name", 2, "nvarchar(100)", true)])).ToList(),
        []);

    private sealed class RecordingHandler : IJobHandler
    {
        public const string Type = "test-recording";

        public List<string> Payloads { get; } = [];

        public int FailNext { get; set; }

        public string JobType => Type;

        public Task HandleAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (FailNext-- > 0)
            {
                throw new InvalidOperationException("simulated failure");
            }

            Payloads.Add(context.Payload);
            return Task.CompletedTask;
        }
    }
}
