using DatabaseAIAgent.Security.Auth;

namespace DatabaseAIAgent.Api.Contracts;

public sealed record EmailRequest(string Email);

public sealed record InvitationTokenRequest(string Token);

public sealed record ChangeRoleRequest(string Role);

public sealed record UpdateRolePermissionsRequest(IReadOnlyList<string> Permissions);

/// <summary>Returned on sign-in/refresh. The refresh token is never in the body; it is set as an httpOnly cookie.</summary>
public sealed record SessionResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, CurrentUserDto User)
{
    public static SessionResponse From(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(session.AccessToken, session.AccessTokenExpiresAt, session.User);
    }
}

public sealed record MessageResponse(string Message);
