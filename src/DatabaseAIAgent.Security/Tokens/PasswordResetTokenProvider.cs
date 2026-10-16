using DatabaseAIAgent.Domain.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Security.Tokens;

public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions()
    {
        Name = PasswordResetTokenProvider.ProviderName;
    }
}

/// <summary>
/// Password-reset tokens get their own, shorter lifetime than email-verification tokens. Reset tokens are bound to
/// the security stamp, so they become invalid as soon as the password changes (single use).
/// </summary>
public sealed class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<User>> logger)
    : DataProtectorTokenProvider<User>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "PasswordReset";
}
