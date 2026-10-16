using DatabaseAIAgent.Application.Abstractions.Authorization;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace DatabaseAIAgent.Security.Authorization;

/// <summary>
/// Resolves permissions from the database with a short in-process cache. Changes made through this instance
/// invalidate immediately; other instances (scale-out) observe changes within <see cref="CacheDuration"/>.
/// </summary>
internal sealed class CachedPermissionService(IServiceScopeFactory scopeFactory, IMemoryCache cache) : IPermissionService, IDisposable
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly Lock _resetLock = new();
    private CancellationTokenSource _reset = new();

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(Key(userId), out IReadOnlySet<string>? cached) && cached is not null)
        {
            return cached;
        }

        CancellationToken resetToken;
        lock (_resetLock)
        {
            resetToken = _reset.Token;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRolePermissionRepository>();
        var permissions = (await repository.GetPermissionsForActiveUserAsync(userId, cancellationToken)).ToHashSet(StringComparer.Ordinal);

        cache.Set(Key(userId), (IReadOnlySet<string>)permissions, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(CacheDuration)
            .AddExpirationToken(new CancellationChangeToken(resetToken)));
        return permissions;
    }

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));

    public void InvalidateAll()
    {
        CancellationTokenSource previous;
        lock (_resetLock)
        {
            previous = _reset;
            _reset = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        lock (_resetLock)
        {
            _reset.Dispose();
        }
    }

    private static string Key(Guid userId) => $"permissions:{userId:N}";
}
