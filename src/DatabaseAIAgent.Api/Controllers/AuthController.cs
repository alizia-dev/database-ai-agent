using DatabaseAIAgent.Api.Authentication;
using DatabaseAIAgent.Api.Configuration;
using DatabaseAIAgent.Api.Contracts;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Security.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DatabaseAIAgent.Api.Controllers;

/// <summary>Public authentication endpoints. Responses never reveal whether an email address has an account.</summary>
[ApiController]
[Route("api/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(AuthService auth) : ControllerBase
{
    private const string CheckEmailMessage = "If the address can be used, we've sent an email with the next step. Please check your inbox.";

    [HttpPost("register")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<MessageResponse>> Register(RegisterCommand command, CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(command, cancellationToken);
        return result.IsSuccess ? Accepted(new MessageResponse(CheckEmailMessage)) : this.ToProblem(result.Error);
    }

    [HttpPost("verify-email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> VerifyEmail(VerifyEmailCommand command, CancellationToken cancellationToken) =>
        this.ToNoContent(await auth.VerifyEmailAsync(command, cancellationToken));

    [HttpPost("resend-verification")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<MessageResponse>> ResendVerification(EmailRequest request, CancellationToken cancellationToken)
    {
        await auth.ResendVerificationAsync(request.Email, cancellationToken);
        return Accepted(new MessageResponse(CheckEmailMessage));
    }

    [HttpPost("login")]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionResponse>> Login(LoginCommand command, CancellationToken cancellationToken) =>
        IssueSession(await auth.LoginAsync(command, cancellationToken));

    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    [ProducesResponseType<SessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionResponse>> Refresh(CancellationToken cancellationToken)
    {
        if (!RefreshTokenCookie.HasCsrfHeader(Request))
        {
            return this.ToProblem(Error.Validation("auth.missing_csrf_header", "The request is missing a required header."));
        }

        var result = await auth.RefreshAsync(RefreshTokenCookie.Read(Request), cancellationToken);
        if (!result.IsSuccess && result.Error.Type == ErrorType.Unauthorized)
        {
            RefreshTokenCookie.Clear(Response);
        }

        return IssueSession(result);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Logout(CancellationToken cancellationToken)
    {
        if (!RefreshTokenCookie.HasCsrfHeader(Request))
        {
            return this.ToProblem(Error.Validation("auth.missing_csrf_header", "The request is missing a required header."));
        }

        await auth.LogoutAsync(RefreshTokenCookie.Read(Request), cancellationToken);
        RefreshTokenCookie.Clear(Response);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<MessageResponse>> ForgotPassword(EmailRequest request, CancellationToken cancellationToken)
    {
        await auth.ForgotPasswordAsync(request.Email, cancellationToken);
        return Accepted(new MessageResponse(CheckEmailMessage));
    }

    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResetPassword(ResetPasswordCommand command, CancellationToken cancellationToken) =>
        this.ToNoContent(await auth.ResetPasswordAsync(command, cancellationToken));

    private ActionResult<SessionResponse> IssueSession(Result<AuthSession> result)
    {
        if (!result.IsSuccess)
        {
            return this.ToProblem(result.Error);
        }

        RefreshTokenCookie.Set(Response, result.Value.RefreshToken, result.Value.RefreshTokenExpiresAt);
        return Ok(SessionResponse.From(result.Value));
    }
}
