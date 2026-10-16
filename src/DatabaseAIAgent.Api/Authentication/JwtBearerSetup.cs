using DatabaseAIAgent.Security;
using DatabaseAIAgent.Security.Sessions;
using DatabaseAIAgent.Security.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DatabaseAIAgent.Api.Authentication;

/// <summary>
/// Configures bearer-token validation from <see cref="JwtOptions"/> and rejects tokens whose session is no longer
/// valid (user deactivated, password changed, signed out everywhere).
/// </summary>
internal sealed class JwtBearerSetup(IOptions<JwtOptions> jwtOptions) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        var settings = jwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKey = AccessTokenIssuer.CreateSigningKey(settings),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AuthClaimTypes.Name,
            RoleClaimType = AuthClaimTypes.Role,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var validator = context.HttpContext.RequestServices.GetRequiredService<SessionValidator>();
                var valid = Guid.TryParse(principal?.FindFirst(AuthClaimTypes.Subject)?.Value, out var userId)
                    && await validator.IsValidAsync(userId, principal?.FindFirst(AuthClaimTypes.SecurityStamp)?.Value, context.HttpContext.RequestAborted);
                if (!valid)
                {
                    context.Fail("Session is no longer valid.");
                }
            },
        };
    }

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);
}
