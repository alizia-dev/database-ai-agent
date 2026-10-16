using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Email;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Infrastructure.Email;
using DatabaseAIAgent.Infrastructure.Persistence;
using DatabaseAIAgent.Infrastructure.Persistence.Interceptors;
using DatabaseAIAgent.Infrastructure.Persistence.Repositories;
using DatabaseAIAgent.Infrastructure.Time;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Infrastructure;

public static class DependencyInjection
{
    public const string DataProtectionApplicationName = "database-ai-agent";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();

        services.AddPlatformDatabase(configuration);
        services.AddIdentityStores();
        services.AddEmail(configuration);

        return services;
    }

    private static void AddPlatformDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PlatformDatabaseOptions>()
            .Bind(configuration.GetSection(PlatformDatabaseOptions.SectionName))
            .PostConfigure(options =>
            {
                // ConnectionStrings:Platform (env: ConnectionStrings__Platform) takes precedence when supplied.
                var fromConnectionStrings = configuration.GetConnectionString(PlatformDatabaseOptions.ConnectionStringName);
                if (!string.IsNullOrWhiteSpace(fromConnectionStrings))
                {
                    options.ConnectionString = fromConnectionStrings;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<AuditableEntityInterceptor>();
        services.AddDbContext<PlatformDbContext>((provider, options) =>
        {
            var dbOptions = provider.GetRequiredService<IOptions<PlatformDatabaseOptions>>().Value;
            options.UseNpgsql(dbOptions.ConnectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", PlatformDbContext.Schema);
                npgsql.CommandTimeout(dbOptions.CommandTimeoutSeconds);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            });
            options.AddInterceptors(provider.GetRequiredService<AuditableEntityInterceptor>());
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IUserActivityRepository, UserActivityRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IUserInvitationRepository, UserInvitationRepository>();
        services.AddScoped<IRolePermissionRepository, RolePermissionRepository>();
    }

    /// <summary>
    /// Registers ASP.NET Core Identity persistence (users, roles, tokens) on the platform database. Identity
    /// behaviour (password, lockout and token policies) is configured by the Security module.
    /// Data-protection keys (used for email/reset tokens) are persisted in the platform database so tokens survive
    /// restarts and work across instances; protect them at rest with a key-encryption key in production.
    /// </summary>
    private static void AddIdentityStores(this IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<PlatformDbContext>();

        services.AddIdentityCore<User>()
            .AddRoles<Role>()
            .AddEntityFrameworkStores<PlatformDbContext>()
            .AddDefaultTokenProviders();
    }

    private static void AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.Transport != EmailTransport.Smtp || !string.IsNullOrWhiteSpace(o.SmtpHost), "Email:SmtpHost is required for SMTP transport.")
            .ValidateOnStart();

        services.AddScoped<SmtpEmailSender>();
        services.AddScoped<PickupDirectoryEmailSender>();
        services.AddScoped<IEmailSender>(provider =>
            provider.GetRequiredService<IOptions<EmailOptions>>().Value.Transport == EmailTransport.PickupDirectory
                ? provider.GetRequiredService<PickupDirectoryEmailSender>()
                : provider.GetRequiredService<SmtpEmailSender>());
    }
}
