using Microsoft.AspNetCore.Identity;

namespace DatabaseAIAgent.Domain.Users;

/// <summary>
/// A person who can sign in to this workspace. Built on the ASP.NET Core Identity user (password hash,
/// lockout, security stamp, email confirmation, two-factor and external-login support).
/// </summary>
public sealed class User : IdentityUser<Guid>
{
    public const int DisplayNameMaxLength = 100;

    public User()
    {
        Id = Guid.CreateVersion7();
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Deactivated users cannot sign in and their existing sessions are rejected.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
