using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Infrastructure.Persistence;
using DatabaseAIAgent.Infrastructure.Persistence.Repositories;
using DatabaseAIAgent.Security;
using DatabaseAIAgent.Security.Tokens;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DatabaseAIAgent.UnitTests.Security;

public sealed class RefreshTokenServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IClock _clock = Substitute.For<IClock>();
    private DateTimeOffset _now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private PlatformDbContext _db = null!;
    private RefreshTokenService _service = null!;
    private Guid _userId;

    public async ValueTask InitializeAsync()
    {
        _clock.UtcNow.Returns(_ => _now);
        await _connection.OpenAsync();
        _db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        var user = new User { Email = "a@example.test", UserName = "a@example.test", NormalizedEmail = "A@EXAMPLE.TEST", DisplayName = "A" };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _userId = user.Id;

        _service = new RefreshTokenService(
            new RefreshTokenRepository(_db),
            new UnitOfWork(_db),
            Substitute.For<ICurrentUser>(),
            _clock,
            Options.Create(new AuthOptions { RefreshTokenDays = 14 }),
            NullLogger<RefreshTokenService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task OnlyTheHashIsStored()
    {
        var issued = await IssueAsync();

        var stored = await _db.RefreshTokens.SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(issued.Token, stored.TokenHash);
        Assert.Equal(OpaqueToken.Hash(issued.Token), stored.TokenHash);
    }

    [Fact]
    public async Task Rotate_IssuesSuccessorInSameFamilyAndRevokesPresentedToken()
    {
        var issued = await IssueAsync();

        var rotated = await _service.RotateAsync(issued.Token, TestContext.Current.CancellationToken);

        Assert.True(rotated.IsSuccess);
        Assert.Equal(_userId, rotated.Value.UserId);
        var tokens = await _db.RefreshTokens.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens.Select(t => t.FamilyId).Distinct());
        Assert.Equal(RefreshTokenRevocationReason.Rotated, tokens.Single(t => t.TokenHash == OpaqueToken.Hash(issued.Token)).RevocationReason);
    }

    [Fact]
    public async Task ReplayWithinGraceWindow_IsAConflictAndDoesNotRevokeTheFamily()
    {
        var issued = await IssueAsync();
        var rotated = await _service.RotateAsync(issued.Token, TestContext.Current.CancellationToken);

        _now = _now.AddSeconds(2);
        var replay = await _service.RotateAsync(issued.Token, TestContext.Current.CancellationToken);

        Assert.Equal(RefreshTokenService.ConcurrentRefresh, replay.Error);
        Assert.True((await _service.RotateAsync(rotated.Value.Successor.Token, TestContext.Current.CancellationToken)).IsSuccess);
    }

    [Fact]
    public async Task ReplayAfterGraceWindow_IsTreatedAsTheftAndRevokesTheWholeFamily()
    {
        var issued = await IssueAsync();
        var rotated = await _service.RotateAsync(issued.Token, TestContext.Current.CancellationToken);

        _now = _now.AddMinutes(1);
        var replay = await _service.RotateAsync(issued.Token, TestContext.Current.CancellationToken);
        var successorAfterTheft = await _service.RotateAsync(rotated.Value.Successor.Token, TestContext.Current.CancellationToken);

        Assert.Equal(RefreshTokenService.InvalidToken, replay.Error);
        Assert.Equal(RefreshTokenService.InvalidToken, successorAfterTheft.Error);
        Assert.Contains(await _db.RefreshTokens.ToListAsync(TestContext.Current.CancellationToken), t => t.RevocationReason == RefreshTokenRevocationReason.ReuseDetected);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        var issued = await IssueAsync();

        _now = _now.AddDays(15);

        Assert.Equal(RefreshTokenService.InvalidToken, (await _service.RotateAsync(issued.Token, TestContext.Current.CancellationToken)).Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("contains spaces and is definitely long enough to pass length")]
    public async Task MalformedToken_IsRejectedWithoutLookup(string? token) =>
        Assert.Equal(RefreshTokenService.InvalidToken, (await _service.RotateAsync(token, TestContext.Current.CancellationToken)).Error);

    [Fact]
    public async Task RevokeFamilyOf_EndsTheSessionOnThisDeviceOnly()
    {
        var device1 = await IssueAsync();
        var device2 = await IssueAsync();

        await _service.RevokeFamilyOfAsync(device1.Token, TestContext.Current.CancellationToken);

        Assert.False((await _service.RotateAsync(device1.Token, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await _service.RotateAsync(device2.Token, TestContext.Current.CancellationToken)).IsSuccess);
    }

    private async Task<IssuedRefreshToken> IssueAsync()
    {
        var issued = _service.Issue(_userId);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return issued;
    }
}
