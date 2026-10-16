using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Security.Tokens;

namespace DatabaseAIAgent.Api.Authentication;

/// <summary>Reads the caller's identity from the validated access token only.</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var principal = accessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return Guid.TryParse(principal.FindFirst(AuthClaimTypes.Subject)?.Value, out var id) ? id : null;
        }
    }

    public bool IsAuthenticated => UserId is not null;

    public string? Email => IsAuthenticated ? accessor.HttpContext?.User.FindFirst(AuthClaimTypes.Email)?.Value : null;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();
}
