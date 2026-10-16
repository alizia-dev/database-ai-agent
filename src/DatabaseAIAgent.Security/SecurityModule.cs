using System.Security.Cryptography;
using DatabaseAIAgent.Application.Abstractions.Authorization;
using DatabaseAIAgent.Application.Abstractions.Security;
using DatabaseAIAgent.Security.Auth;
using DatabaseAIAgent.Security.Authorization;
using DatabaseAIAgent.Security.Email;
using DatabaseAIAgent.Security.Encryption;
using DatabaseAIAgent.Security.Sessions;
using DatabaseAIAgent.Security.Tokens;
using DatabaseAIAgent.Security.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Security;

/// <summary>Composition entry point for authentication, users, roles and permissions.</summary>
public static class SecurityModule
{
    public static IServiceCollection AddSecurityModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .PostConfigure<ILoggerFactory>((options, loggers) =>
            {
                // Local development may run without a configured key: use an ephemeral one (access tokens become invalid
                // on restart; refresh tokens are database-backed and keep working). Never allowed outside Development.
                if (string.IsNullOrEmpty(options.SigningKey) && environment.IsDevelopment())
                {
                    options.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                    loggers.CreateLogger(typeof(SecurityModule)).LogWarning("Jwt:SigningKey is not configured; using an ephemeral development key");
                }
            })
            .ValidateDataAnnotations()
            .Validate(o => o.SigningKeyBytes.Length >= JwtOptions.MinimumKeyBytes, $"Jwt:SigningKey must be at least {JwtOptions.MinimumKeyBytes} bytes (set JWT_SECRET).")
            .ValidateOnStart();

        services.AddSecretProtection(configuration, environment);

        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<IdentityOptions>(identity =>
        {
            // Length over composition rules (NIST SP 800-63B).
            identity.Password.RequiredLength = 12;
            identity.Password.RequiredUniqueChars = 4;
            identity.Password.RequireDigit = false;
            identity.Password.RequireLowercase = false;
            identity.Password.RequireUppercase = false;
            identity.Password.RequireNonAlphanumeric = false;

            identity.Lockout.AllowedForNewUsers = true;
            identity.Lockout.MaxFailedAccessAttempts = 5;
            identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

            identity.User.RequireUniqueEmail = true;
            identity.SignIn.RequireConfirmedEmail = auth.RequireVerifiedEmail;

            identity.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName;
            identity.Tokens.ProviderMap[PasswordResetTokenProvider.ProviderName] = new TokenProviderDescriptor(typeof(PasswordResetTokenProvider));

            identity.ClaimsIdentity.UserIdClaimType = AuthClaimTypes.Subject;
            identity.ClaimsIdentity.EmailClaimType = AuthClaimTypes.Email;
            identity.ClaimsIdentity.RoleClaimType = AuthClaimTypes.Role;
            identity.ClaimsIdentity.SecurityStampClaimType = AuthClaimTypes.SecurityStamp;
        });
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(auth.EmailVerificationTokenDays));
        services.Configure<PasswordResetTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromMinutes(auth.PasswordResetTokenMinutes));
        services.AddTransient<PasswordResetTokenProvider>();

        services.AddMemoryCache();
        services.AddSingleton<IPermissionService, CachedPermissionService>();
        services.AddSingleton<SessionValidator>();

        services.AddScoped<AccessTokenIssuer>();
        services.AddScoped<RefreshTokenService>();
        services.AddScoped<AccountEmails>();
        services.AddScoped<UserDirectory>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserManagementService>();
        services.AddScoped<RoleService>();

        return services;
    }

    /// <summary>
    /// Encryption of stored secrets (customer database credentials). Also used by the Workers host, which needs to
    /// decrypt credentials but does not authenticate users.
    /// </summary>
    public static IServiceCollection AddSecretProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<EncryptionOptions>()
            .Bind(configuration.GetSection(EncryptionOptions.SectionName))
            .PostConfigure<ILoggerFactory>((options, loggers) =>
            {
                if (string.IsNullOrEmpty(options.Key) && environment.IsDevelopment())
                {
                    options.Key = DevelopmentEncryptionKey.LoadOrCreate();
                    loggers.CreateLogger(typeof(SecurityModule)).LogWarning("Encryption:Key is not configured; using the per-machine development key");
                }
            })
            .Validate(o => EncryptionOptions.IsValidKey(o.Key), "Encryption:Key must be 32 random bytes, base64-encoded (set ENCRYPTION_KEY).")
            .ValidateOnStart();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        return services;
    }
}
