using System.ComponentModel.DataAnnotations;
using System.Text;

namespace DatabaseAIAgent.Security;

/// <summary>Access-token (JWT) settings. <see cref="SigningKey"/> comes from <c>JWT_SECRET</c> / <c>Jwt__SigningKey</c>.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const int MinimumKeyBytes = 32;

    [Required]
    public string Issuer { get; set; } = "database-ai-agent";

    [Required]
    public string Audience { get; set; } = "database-ai-agent";

    [Required]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 10;

    internal byte[] SigningKeyBytes => Encoding.UTF8.GetBytes(SigningKey);
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Users must confirm their email address before they can sign in.</summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>
    /// Single-tenant deployments are invitation-only once an Owner exists. When true, anyone may still register
    /// and joins as <c>Viewer</c>.
    /// </summary>
    public bool AllowPublicRegistration { get; set; }

    [Range(1, 30)]
    public int InvitationDays { get; set; } = 7;

    [Range(5, 1440)]
    public int PasswordResetTokenMinutes { get; set; } = 60;

    [Range(1, 30)]
    public int EmailVerificationTokenDays { get; set; } = 2;
}

