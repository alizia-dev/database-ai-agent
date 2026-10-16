using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Security.Tokens;

public sealed record IssuedRefreshToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Rotating refresh tokens with reuse detection. Each refresh revokes the presented token and issues a successor in
/// the same family. Presenting a token that was already rotated means it was copied: the whole family is revoked.
/// A short grace window tolerates the benign race of two browser tabs refreshing at the same moment.
/// </summary>
public sealed class RefreshTokenService(
    IRefreshTokenRepository tokens,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<AuthOptions> options,
    ILogger<RefreshTokenService> logger)
{
    internal static readonly TimeSpan ConcurrentRefreshGrace = TimeSpan.FromSeconds(10);

    public static readonly Error InvalidToken = Error.Unauthorized("auth.invalid_refresh_token", "Your session has expired. Please sign in again.");

    public static readonly Error ConcurrentRefresh = Error.Conflict("auth.refresh_in_progress", "Your session is being refreshed. Please retry.");

    /// <summary>Creates a token in a new family (sign-in) or in an existing family (rotation). Saved by the caller.</summary>
    public IssuedRefreshToken Issue(Guid userId, Guid? familyId = null)
    {
        var now = clock.UtcNow;
        var (token, hash) = OpaqueToken.Create();
        var expiresAt = now.AddDays(options.Value.RefreshTokenDays);
        tokens.Add(new RefreshToken(userId, hash, familyId ?? Guid.CreateVersion7(), now, expiresAt, Truncate(currentUser.IpAddress, 64)));
        return new IssuedRefreshToken(token, expiresAt);
    }

    /// <summary>Validates and rotates a token, returning the owning user id and the successor token (saved).</summary>
    public async Task<Result<(Guid UserId, IssuedRefreshToken Successor)>> RotateAsync(string? presented, CancellationToken cancellationToken)
    {
        if (!OpaqueToken.LooksValid(presented))
        {
            return InvalidToken;
        }

        var now = clock.UtcNow;
        var token = await tokens.FindByHashAsync(OpaqueToken.Hash(presented!), cancellationToken);
        if (token is null)
        {
            return InvalidToken;
        }

        if (token.IsRevoked)
        {
            if (token.RevocationReason == RefreshTokenRevocationReason.Rotated && token.RevokedAt > now - ConcurrentRefreshGrace)
            {
                return ConcurrentRefresh;
            }

            if (token.RevocationReason == RefreshTokenRevocationReason.Rotated)
            {
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking token family {FamilyId}", token.UserId, token.FamilyId);
                await RevokeFamilyAsync(token.FamilyId, RefreshTokenRevocationReason.ReuseDetected, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return InvalidToken;
        }

        if (!token.IsActive(now))
        {
            return InvalidToken;
        }

        token.Revoke(now, RefreshTokenRevocationReason.Rotated);
        var successor = Issue(token.UserId, token.FamilyId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return (token.UserId, successor);
    }

    /// <summary>Revokes the presented token's family (sign-out on this device). Unknown tokens are ignored.</summary>
    public async Task RevokeFamilyOfAsync(string? presented, CancellationToken cancellationToken)
    {
        if (!OpaqueToken.LooksValid(presented))
        {
            return;
        }

        var token = await tokens.FindByHashAsync(OpaqueToken.Hash(presented!), cancellationToken);
        if (token is not null)
        {
            await RevokeFamilyAsync(token.FamilyId, RefreshTokenRevocationReason.Logout, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Revokes every token of the user (tracked; saved by the caller).</summary>
    public async Task RevokeAllForUserAsync(Guid userId, RefreshTokenRevocationReason reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var token in await tokens.ListUnrevokedByUserAsync(userId, cancellationToken))
        {
            token.Revoke(now, reason);
        }
    }

    private async Task RevokeFamilyAsync(Guid familyId, RefreshTokenRevocationReason reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var token in await tokens.ListUnrevokedByFamilyAsync(familyId, cancellationToken))
        {
            token.Revoke(now, reason);
        }
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
