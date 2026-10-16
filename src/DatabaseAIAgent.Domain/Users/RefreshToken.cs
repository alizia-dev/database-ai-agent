using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Users;

/// <summary>
/// A rotating refresh token. Only a SHA-256 hash of the token is stored. Tokens issued by rotation share a
/// <see cref="FamilyId"/>; presenting an already-rotated token is treated as theft and revokes the whole family.
/// </summary>
public sealed class RefreshToken : Entity
{
    public const int HashLength = 64;

    private RefreshToken()
    {
    }

    public RefreshToken(Guid userId, string tokenHash, Guid familyId, DateTimeOffset createdAt, DateTimeOffset expiresAt, string? createdByIp)
    {
        if (tokenHash.Length != HashLength)
        {
            throw new DomainException("Refresh token hash has an unexpected length.");
        }

        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        CreatedByIp = createdByIp;
    }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public Guid FamilyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public string? CreatedByIp { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public RefreshTokenRevocationReason? RevocationReason { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && now < ExpiresAt;

    public void Revoke(DateTimeOffset now, RefreshTokenRevocationReason reason)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAt = now;
        RevocationReason = reason;
    }
}

public enum RefreshTokenRevocationReason
{
    Rotated = 0,
    Logout = 1,
    ReuseDetected = 2,
    SessionsRevoked = 3,
    UserDeactivated = 4,
}
