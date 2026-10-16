using DatabaseAIAgent.Api.Authentication;
using DatabaseAIAgent.Api.Configuration;
using DatabaseAIAgent.Api.Contracts;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Security.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DatabaseAIAgent.Api.Controllers;

/// <summary>
/// Public invitation endpoints. Tokens travel in request bodies (never in URLs) so they do not end up in access logs.
/// </summary>
[ApiController]
[Route("api/invitations")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class InvitationsController(UserManagementService team) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<ActionResult<InvitationPreviewDto>> Preview(InvitationTokenRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await team.PreviewInvitationAsync(request.Token, cancellationToken));

    [HttpPost("accept")]
    public async Task<ActionResult<SessionResponse>> Accept(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var result = await team.AcceptInvitationAsync(command, cancellationToken);
        if (!result.IsSuccess)
        {
            return this.ToProblem(result.Error);
        }

        RefreshTokenCookie.Set(Response, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(SessionResponse.From(result.Value));
    }
}
