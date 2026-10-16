using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Users;

/// <summary>An invitation for a person to join the workspace with a given role. Only the token hash is stored.</summary>
public sealed class UserInvitation : Entity
{
    public const int EmailMaxLength = 256;

    private UserInvitation()
    {
    }

    public UserInvitation(string email, string role, string tokenHash, Guid invitedByUserId, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        if (!SystemRoles.IsKnown(role) || role == SystemRoles.Owner)
        {
            throw new DomainException("Invitations can grant Admin, Manager, Analyst or Viewer.");
        }

        Email = email.Trim();
        NormalizedEmail = Email.ToUpperInvariant();
        Role = role;
        TokenHash = tokenHash;
        InvitedByUserId = invitedByUserId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string Role { get; private set; } = string.Empty;

    public string TokenHash { get; private set; } = string.Empty;

    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && now < ExpiresAt;

    public void Accept(DateTimeOffset now)
    {
        if (!IsPending(now))
        {
            throw new DomainException("This invitation is no longer valid.");
        }

        AcceptedAt = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        if (AcceptedAt is null)
        {
            RevokedAt ??= now;
        }
    }
}
