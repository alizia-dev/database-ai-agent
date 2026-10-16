using DatabaseAIAgent.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Security.Sessions;

/// <summary>
/// Validates that an access token still represents a live session: the user exists, is active, and the token's
/// security stamp matches the current one (password change, "sign out everywhere" and deactivation rotate the stamp).
/// Results are cached briefly so this does not hit the database on every request.
/// </summary>
public sealed class SessionValidator(IServiceScopeFactory scopeFactory, IMemoryCache cache)
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<bool> IsValidAsync(Guid userId, string? securityStamp, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(securityStamp))
        {
            return false;
        }

        var current = await cache.GetOrCreateAsync(Key(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            await using var scope = scopeFactory.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(userId.ToString());
            return user is { IsActive: true } ? user.SecurityStamp : null;
        });

        cancellationToken.ThrowIfCancellationRequested();
        return current is not null && string.Equals(current, securityStamp, StringComparison.Ordinal);
    }

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));

    private static string Key(Guid userId) => $"session:{userId:N}";
}
