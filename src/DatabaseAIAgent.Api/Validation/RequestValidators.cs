using DatabaseAIAgent.Api.Contracts;
using DatabaseAIAgent.Application.Workspace;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Security.Auth;
using DatabaseAIAgent.Security.Users;
using FluentValidation;

namespace DatabaseAIAgent.Api.Validation;

/// <summary>
/// Request-shape validation (presence, length, format). Business rules such as password strength or role
/// assignment are enforced by the services and Identity.
/// </summary>
internal static class Limits
{
    public const int EmailMaxLength = 256;

    // Upper bound prevents hashing very large inputs (CPU exhaustion); minimum is enforced by the Identity password policy.
    public const int PasswordMaxLength = 128;
    public const int TokenMaxLength = 2048;

    public static IRuleBuilderOptions<T, string> ValidEmail<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Enter your email address.")
            .MaximumLength(EmailMaxLength)
            .EmailAddress().WithMessage("Enter a valid email address.");

    public static IRuleBuilderOptions<T, string> ValidPassword<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Enter a password.").MaximumLength(PasswordMaxLength);

    public static IRuleBuilderOptions<T, string> ValidDisplayName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(v => !string.IsNullOrWhiteSpace(v)).WithMessage("Enter your name.").MaximumLength(User.DisplayNameMaxLength);

    public static IRuleBuilderOptions<T, string> ValidToken<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(TokenMaxLength);
}

internal sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Password).ValidPassword();
        RuleFor(x => x.DisplayName).ValidDisplayName();
    }
}

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(Limits.EmailMaxLength);
        RuleFor(x => x.Password).ValidPassword();
    }
}

internal sealed class EmailRequestValidator : AbstractValidator<EmailRequest>
{
    public EmailRequestValidator() => RuleFor(x => x.Email).ValidEmail();
}

internal sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Token).ValidToken();
    }
}

internal sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Token).ValidToken();
        RuleFor(x => x.NewPassword).ValidPassword();
    }
}

internal sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).ValidPassword();
        RuleFor(x => x.NewPassword).ValidPassword()
            .NotEqual(x => x.CurrentPassword).WithMessage("Choose a password different from your current one.");
    }
}

internal sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator() => RuleFor(x => x.DisplayName).ValidDisplayName();
}

internal sealed class InviteUserValidator : AbstractValidator<InviteUserCommand>
{
    public InviteUserValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Role).NotEmpty().MaximumLength(32);
    }
}

internal sealed class InvitationTokenValidator : AbstractValidator<InvitationTokenRequest>
{
    public InvitationTokenValidator() => RuleFor(x => x.Token).ValidToken();
}

internal sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationValidator()
    {
        RuleFor(x => x.Token).ValidToken();
        RuleFor(x => x.DisplayName).ValidDisplayName();
        RuleFor(x => x.Password).ValidPassword();
    }
}

internal sealed class ChangeRoleValidator : AbstractValidator<ChangeRoleRequest>
{
    public ChangeRoleValidator() => RuleFor(x => x.Role).NotEmpty().MaximumLength(32);
}

internal sealed class UpdateRolePermissionsValidator : AbstractValidator<UpdateRolePermissionsRequest>
{
    public UpdateRolePermissionsValidator() => RuleFor(x => x.Permissions).NotNull().Must(p => p.Count <= 100);
}

internal sealed class UpdateWorkspaceSettingsValidator : AbstractValidator<UpdateWorkspaceSettingsRequest>
{
    public UpdateWorkspaceSettingsValidator()
    {
        RuleFor(x => x.Name).NotEmpty();
        RuleFor(x => x.AssistantName).NotEmpty();
        RuleFor(x => x.TimeZone).NotEmpty();
        RuleFor(x => x.Currency).NotEmpty();
        RuleFor(x => x.DateFormat).NotEmpty();
        RuleFor(x => x.ResponseStyle).IsInEnum();
        RuleFor(x => x.Retention).NotNull();
    }
}

internal sealed class ConnectionInputValidator : AbstractValidator<ConnectionInput>
{
    public ConnectionInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name for this connection.").MaximumLength(DatabaseConnection.NameMaxLength);
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.TlsMode).IsInEnum().When(x => x.TlsMode is not null);
        RuleFor(x => x.Host).MaximumLength(DatabaseConnection.HostMaxLength);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
        RuleFor(x => x.Database).MaximumLength(DatabaseConnection.DatabaseNameMaxLength);
        RuleFor(x => x.Username).MaximumLength(128);
        RuleFor(x => x.Password).MaximumLength(256);
        RuleFor(x => x.ConnectionString).MaximumLength(2048);
    }
}
