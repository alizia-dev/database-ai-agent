using System.Security.Claims;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DatabaseAIAgent.Security.Tokens;

public static class AuthClaimTypes
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Name = "name";
    public const string Role = "role";

    /// <summary>The user's Identity security stamp; tokens with a stale stamp are rejected.</summary>
    public const string SecurityStamp = "sstamp";
}

/// <summary>Issues short-lived, HMAC-SHA256 signed access tokens. Tokens carry identity only, never permissions.</summary>
public sealed class AccessTokenIssuer(IOptions<JwtOptions> options, IClock clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user, string role)
    {
        ArgumentNullException.ThrowIfNull(user);
        var settings = options.Value;
        var now = clock.UtcNow;
        var expiresAt = now.AddMinutes(settings.AccessTokenMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = CreateSigningCredentials(settings),
            Subject = new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.Subject, user.Id.ToString()),
                new Claim(AuthClaimTypes.Email, user.Email ?? string.Empty),
                new Claim(AuthClaimTypes.Name, user.DisplayName),
                new Claim(AuthClaimTypes.Role, role),
                new Claim(AuthClaimTypes.SecurityStamp, user.SecurityStamp ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        };

        return (_handler.CreateToken(descriptor), expiresAt);
    }

    public static SymmetricSecurityKey CreateSigningKey(JwtOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new(settings.SigningKeyBytes);
    }

    private static SigningCredentials CreateSigningCredentials(JwtOptions settings) =>
        new(CreateSigningKey(settings), SecurityAlgorithms.HmacSha256);
}
