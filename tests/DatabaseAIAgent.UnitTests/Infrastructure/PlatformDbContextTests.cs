using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Domain.Organizations;
using DatabaseAIAgent.Infrastructure.Persistence;
using DatabaseAIAgent.Infrastructure.Persistence.Interceptors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DatabaseAIAgent.UnitTests.Infrastructure;

/// <summary>
/// Exercises the EF Core model, interceptors and seeding against an in-memory SQLite database (a relational
/// provider, so constraints and complex-type mapping are real). PostgreSQL-specific behaviour is covered by the
/// integration test suite.
/// </summary>
public sealed class PlatformDbContextTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedTime = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedTime = new(2026, 1, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveChanges_StampsCreatedAtOnInsertAndUpdatedAtOnModify()
    {
        var ct = TestContext.Current.CancellationToken;
        _clock.UtcNow.Returns(CreatedTime);
        await using (var db = CreateContext())
        {
            db.Organizations.Add(Organization.CreateDefault());
            await db.SaveChangesAsync(ct);
        }

        _clock.UtcNow.Returns(UpdatedTime);
        await using (var db = CreateContext())
        {
            var organization = await db.Organizations.SingleAsync(ct);
            organization.Update("Acme", null, "Ava", null, "UTC", "EUR", "yyyy-MM-dd", ResponseStyle.Balanced, RetentionPolicy.Default with { ConversationDays = 30 });
            await db.SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var organization = await db.Organizations.SingleAsync(ct);
            Assert.Equal(CreatedTime, organization.CreatedAt);
            Assert.Equal(UpdatedTime, organization.UpdatedAt);
            Assert.Equal("EUR", organization.Currency);
            Assert.Equal(30, organization.Retention.ConversationDays);
        }
    }

    [Fact]
    public async Task SeedOrganization_IsIdempotentAndPreservesAdministratorChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        _clock.UtcNow.Returns(CreatedTime);
        await using (var db = CreateContext())
        {
            await PlatformDatabaseInitializer.SeedOrganizationAsync(db, NullLogger.Instance, ct);
            var organization = await db.Organizations.SingleAsync(ct);
            organization.Update("Acme", null, "Ava", null, "UTC", "USD", "yyyy-MM-dd", ResponseStyle.Balanced, RetentionPolicy.Default);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            await PlatformDatabaseInitializer.SeedOrganizationAsync(db, NullLogger.Instance, ct);

            var organizations = await db.Organizations.ToListAsync(ct);
            var organization = Assert.Single(organizations);
            Assert.Equal("Acme", organization.Name);
        }
    }

    [Fact]
    public async Task Database_RejectsASecondOrganizationRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = CreateContext();
        await PlatformDatabaseInitializer.SeedOrganizationAsync(db, NullLogger.Instance, ct);

        // Bypass the domain model to prove the database itself enforces the single-tenant singleton.
        var insert = db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO organization ("Id", "Name", "AssistantName", "TimeZone", "Currency", "DateFormat", "ResponseStyle", "CreatedAt")
            VALUES ('00000000-0000-0000-0000-000000000002', 'Other', 'Bot', 'UTC', 'USD', 'yyyy-MM-dd', 'Balanced', '2026-01-01')
            """,
            ct);

        await Assert.ThrowsAsync<SqliteException>(() => insert);
    }

    private PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditableEntityInterceptor(_clock))
            .Options;
        return new PlatformDbContext(options);
    }
}
