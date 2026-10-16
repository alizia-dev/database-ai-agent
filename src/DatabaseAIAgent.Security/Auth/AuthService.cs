using System.Text;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Authorization;
using DatabaseAIAgent.Application.Abstractions.Email;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Security.Email;
using DatabaseAIAgent.Security.Sessions;
using DatabaseAIAgent.Security.Tokens;
using DatabaseAIAgent.Security.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Security.Auth;

public sealed class AuthService(
    UserManager<User> users,
    UserDirectory directory,
    AccessTokenIssuer accessTokens,
    RefreshTokenService refreshTokens,
    IPermissionService permissions,
    SessionValidator sessions,
    IEmailSender emailSender,
    AccountEmails emails,
    IUnitOfWork unitOfWork,
    IUserActivityRepository activity,
    IClock clock,
    IOptions<AuthOptions> options,
    ILogger<AuthService> logger)
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "The email or password is incorrect.");

    private static readonly Error LockedOut = new("auth.locked_out", "Too many failed sign-in attempts. Please try again later or reset your password.", ErrorType.TooManyRequests);
    private static readonly Error EmailNotVerified = Error.Forbidden("auth.email_not_verified", "Please verify your email address before signing in. We can send you a new link.");
    private static readonly Error AccountDisabled = Error.Forbidden("auth.account_disabled", "This account has been deactivated. Contact your workspace administrator.");
    private static readonly Error RegistrationClosed = Error.Forbidden("auth.registration_closed", "Registration is by invitation only. Ask your workspace administrator to invite you.");
    private static readonly Error InvalidLink = Error.Validation("auth.invalid_link", "This link is invalid or has expired. Please request a new one.");

    // Serializes first-user bootstrap so two simultaneous registrations cannot both become Owner (single API instance;
    // the unique Owner bootstrap only happens once per deployment, during setup).
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);

    // Used to keep response time similar when the email is unknown, so timing does not reveal account existence.
    private static readonly Lazy<string> DummyPasswordHash = new(() => new PasswordHasher<User>().HashPassword(new User(), Guid.NewGuid().ToString()));

    public async Task<Result<RegistrationOutcome>> RegisterAsync(RegisterCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var email = command.Email.Trim();

        await RegistrationGate.WaitAsync(cancellationToken);
        try
        {
            var ownerExists = await directory.OwnerExistsAsync();
            if (ownerExists && !options.Value.AllowPublicRegistration)
            {
                return RegistrationClosed;
            }

            var existing = await users.FindByEmailAsync(email);
            if (existing is not null)
            {
                await (existing.EmailConfirmed
                    ? emailSender.SendAsync(emails.AccountAlreadyExists(existing.Email!), cancellationToken)
                    : SendVerificationEmailAsync(existing, cancellationToken));
                return RegistrationOutcome.VerificationEmailSent;
            }

            var user = new User
            {
                Email = email,
                UserName = email,
                DisplayName = command.DisplayName.Trim(),
                CreatedAt = clock.UtcNow,
                EmailConfirmed = !options.Value.RequireVerifiedEmail,
            };

            var role = ownerExists ? SystemRoles.Viewer : SystemRoles.Owner;
            var created = await unitOfWork.ExecuteInTransactionAsync(
                async _ =>
                {
                    var result = await users.CreateAsync(user, command.Password);
                    return result.Succeeded ? await users.AddToRoleAsync(user, role) : result;
                },
                result => result.Succeeded,
                cancellationToken);

            if (!created.Succeeded)
            {
                return Error.Validation("auth.registration_invalid", "Please correct the highlighted fields.", ToRegistrationFieldErrors(created.Errors));
            }

            logger.LogInformation("Registered user {UserId} with role {Role}", user.Id, role);
            if (!user.EmailConfirmed)
            {
                await SendVerificationEmailAsync(user, cancellationToken);
            }

            return RegistrationOutcome.VerificationEmailSent;
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    public async Task<Result> VerifyEmailAsync(VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindByIdAsync(command.UserId.ToString());
        if (user is null || !TryDecode(command.Token, out var token))
        {
            return InvalidLink;
        }

        if (user.EmailConfirmed)
        {
            return Result.Success();
        }

        var result = await users.ConfirmEmailAsync(user, token);
        return result.Succeeded ? Result.Success() : InvalidLink;
    }

    /// <summary>Always succeeds (no account enumeration); sends a new link only to unverified, active accounts.</summary>
    public async Task ResendVerificationAsync(string email, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is { EmailConfirmed: false, IsActive: true })
        {
            await SendVerificationEmailAsync(user, cancellationToken);
        }
    }

    public async Task<Result<AuthSession>> LoginAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindByEmailAsync(command.Email.Trim());
        if (user is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(new User(), DummyPasswordHash.Value, command.Password);
            return InvalidCredentials;
        }

        if (await users.IsLockedOutAsync(user))
        {
            return LockedOut;
        }

        if (!await users.CheckPasswordAsync(user, command.Password))
        {
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user))
            {
                logger.LogWarning("User {UserId} locked out after repeated failed sign-in attempts", user.Id);
                return LockedOut;
            }

            return InvalidCredentials;
        }

        if (!user.IsActive)
        {
            return AccountDisabled;
        }

        if (!user.EmailConfirmed && options.Value.RequireVerifiedEmail)
        {
            return EmailNotVerified;
        }

        // Single-row update that also resets the failed-attempt counter. The tracked user entity is deliberately not
        // modified: saving it would go through Identity's concurrency stamp and conflict with parallel sign-ins.
        await activity.RecordSignInAsync(user.Id, clock.UtcNow, cancellationToken);
        logger.LogInformation("User {UserId} signed in", user.Id);
        return await CreateSessionAsync(user, familyId: null, cancellationToken);
    }

    public async Task<Result<AuthSession>> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        var rotated = await refreshTokens.RotateAsync(refreshToken, cancellationToken);
        if (!rotated.IsSuccess)
        {
            return rotated.Error;
        }

        var (userId, successor) = rotated.Value;
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not { IsActive: true })
        {
            await refreshTokens.RevokeAllForUserAsync(userId, RefreshTokenRevocationReason.UserDeactivated, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return RefreshTokenService.InvalidToken;
        }

        return await BuildSessionAsync(user, successor, cancellationToken);
    }

    public Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken) =>
        refreshTokens.RevokeFamilyOfAsync(refreshToken, cancellationToken);

    /// <summary>Signs the user out of every device: revokes all refresh tokens and rotates the security stamp.</summary>
    public async Task<Result> LogoutEverywhereAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return RefreshTokenService.InvalidToken;
        }

        await refreshTokens.RevokeAllForUserAsync(userId, RefreshTokenRevocationReason.SessionsRevoked, cancellationToken);
        UserDirectory.EnsureSucceeded(await users.UpdateSecurityStampAsync(user), "update-security-stamp");
        sessions.Invalidate(userId);
        return Result.Success();
    }

    /// <summary>Always succeeds (no account enumeration); emails a reset link to active accounts only.</summary>
    public async Task ForgotPasswordAsync(string email, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is not { IsActive: true })
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        await emailSender.SendAsync(emails.ResetPassword(user.Email!, user.DisplayName, Encode(token)), cancellationToken);
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindByEmailAsync(command.Email.Trim());
        if (user is not { IsActive: true } || !TryDecode(command.Token, out var token))
        {
            return InvalidLink;
        }

        var result = await users.ResetPasswordAsync(user, token, command.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                ? InvalidLink
                : Error.Validation("auth.password_invalid", "Choose a stronger password.", UserDirectory.ToFieldErrors(result.Errors, "newPassword"));
        }

        // A successful reset proves control of the mailbox; clear lockout and end every existing session.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
        }

        UserDirectory.EnsureSucceeded(await users.SetLockoutEndDateAsync(user, null), "clear-lockout");
        UserDirectory.EnsureSucceeded(await users.ResetAccessFailedCountAsync(user), "reset-access-failed");
        await refreshTokens.RevokeAllForUserAsync(user.Id, RefreshTokenRevocationReason.SessionsRevoked, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        sessions.Invalidate(user.Id);
        await emailSender.SendAsync(emails.PasswordChanged(user.Email!, user.DisplayName), cancellationToken);
        return Result.Success();
    }

    /// <summary>Changes the password, signs out every other session, and returns a fresh session for this device.</summary>
    public async Task<Result<AuthSession>> ChangePasswordAsync(Guid userId, ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not { IsActive: true })
        {
            return RefreshTokenService.InvalidToken;
        }

        var result = await users.ChangePasswordAsync(user, command.CurrentPassword, command.NewPassword);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch))
                ? Error.Validation("auth.current_password_incorrect", "Your current password is incorrect.", new Dictionary<string, string[]> { ["currentPassword"] = ["Your current password is incorrect."] })
                : Error.Validation("auth.password_invalid", "Choose a stronger password.", UserDirectory.ToFieldErrors(result.Errors, "newPassword"));
        }

        await refreshTokens.RevokeAllForUserAsync(user.Id, RefreshTokenRevocationReason.SessionsRevoked, cancellationToken);
        sessions.Invalidate(user.Id);
        var session = await CreateSessionAsync(user, familyId: null, cancellationToken);
        await emailSender.SendAsync(emails.PasswordChanged(user.Email!, user.DisplayName), cancellationToken);
        return session;
    }

    public async Task<Result<CurrentUserDto>> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is { IsActive: true }
            ? await ToDtoAsync(user, cancellationToken)
            : RefreshTokenService.InvalidToken;
    }

    public async Task<Result<CurrentUserDto>> UpdateProfileAsync(Guid userId, UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is not { IsActive: true })
        {
            return RefreshTokenService.InvalidToken;
        }

        user.DisplayName = command.DisplayName.Trim();
        UserDirectory.EnsureSucceeded(await users.UpdateAsync(user), "update-profile");
        return await ToDtoAsync(user, cancellationToken);
    }

    private async Task<AuthSession> CreateSessionAsync(User user, Guid? familyId, CancellationToken cancellationToken)
    {
        var refresh = refreshTokens.Issue(user.Id, familyId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await BuildSessionAsync(user, refresh, cancellationToken);
    }

    private async Task<AuthSession> BuildSessionAsync(User user, IssuedRefreshToken refresh, CancellationToken cancellationToken)
    {
        var dto = await ToDtoAsync(user, cancellationToken);
        var (accessToken, expiresAt) = accessTokens.Issue(user, dto.Role);
        return new AuthSession(accessToken, expiresAt, refresh.Token, refresh.ExpiresAt, dto);
    }

    private async Task<CurrentUserDto> ToDtoAsync(User user, CancellationToken cancellationToken)
    {
        var role = await directory.GetRoleAsync(user);
        var granted = await permissions.GetPermissionsAsync(user.Id, cancellationToken);
        return new CurrentUserDto(user.Id, user.Email!, user.DisplayName, role, Permissions.All.Where(granted.Contains).ToList());
    }

    private async Task SendVerificationEmailAsync(User user, CancellationToken cancellationToken)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await emailSender.SendAsync(emails.VerifyEmail(user.Email!, user.DisplayName, user.Id, Encode(token)), cancellationToken);
    }

    private static Dictionary<string, string[]> ToRegistrationFieldErrors(IEnumerable<IdentityError> errors)
    {
        var fields = new Dictionary<string, List<string>>();
        foreach (var error in errors)
        {
            var field = error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password"
                : error.Code.Contains("Email", StringComparison.Ordinal) || error.Code.Contains("UserName", StringComparison.Ordinal) ? "email"
                : "form";
            if (!fields.TryGetValue(field, out var list))
            {
                fields[field] = list = [];
            }

            list.Add(error.Description);
        }

        return fields.ToDictionary(f => f.Key, f => f.Value.ToArray());
    }

    internal static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static bool TryDecode(string? encoded, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 2048)
        {
            return false;
        }

        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(encoded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
