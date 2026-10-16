namespace DatabaseAIAgent.Security.Auth;

public sealed record RegisterCommand(string Email, string Password, string DisplayName);

public sealed record LoginCommand(string Email, string Password);

public sealed record VerifyEmailCommand(Guid UserId, string Token);

public sealed record ResetPasswordCommand(string Email, string Token, string NewPassword);

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword);

public sealed record UpdateProfileCommand(string DisplayName);

public sealed record CurrentUserDto(Guid Id, string Email, string DisplayName, string Role, IReadOnlyList<string> Permissions);

/// <summary>A signed-in session: the access token is returned to the client; the refresh token goes into an httpOnly cookie.</summary>
public sealed record AuthSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    CurrentUserDto User);

public enum RegistrationOutcome
{
    /// <summary>Returned for new and existing emails alike, so registration cannot be used to enumerate accounts.</summary>
    VerificationEmailSent = 0,
}
