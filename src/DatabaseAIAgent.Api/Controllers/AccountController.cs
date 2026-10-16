using DatabaseAIAgent.Api.Authentication;
using DatabaseAIAgent.Api.Contracts;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Security.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

/// <summary>The signed-in user's own account. The user id always comes from the access token.</summary>
[ApiController]
[Route("api/account")]
[Authorize]
public sealed class AccountController(AuthService auth, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserDto>> Me(CancellationToken cancellationToken) =>
        this.ToActionResult(await auth.GetCurrentUserAsync(currentUser.RequiredUserId, cancellationToken));

    [HttpPut("profile")]
    [ProducesResponseType<CurrentUserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CurrentUserDto>> UpdateProfile(UpdateProfileCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await auth.UpdateProfileAsync(currentUser.RequiredUserId, command, cancellationToken));

    /// <summary>Changes the password, signs out all other sessions, and returns a new session for this device.</summary>
    [HttpPost("change-password")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionResponse>> ChangePassword(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await auth.ChangePasswordAsync(currentUser.RequiredUserId, command, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToProblem(result.Error);
        }

        RefreshTokenCookie.Set(Response, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(SessionResponse.From(result.Value));
    }

    [HttpPost("logout-everywhere")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> LogoutEverywhere(CancellationToken cancellationToken)
    {
        var result = await auth.LogoutEverywhereAsync(currentUser.RequiredUserId, cancellationToken);
        RefreshTokenCookie.Clear(Response);
        return this.ToNoContent(result);
    }
}
