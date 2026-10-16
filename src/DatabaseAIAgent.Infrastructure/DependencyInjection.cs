using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Infrastructure.Persistence;
using DatabaseAIAgent.Infrastructure.Persistence.Interceptors;
using DatabaseAIAgent.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, SystemClock>();

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
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform");
                npgsql.CommandTimeout(dbOptions.CommandTimeoutSeconds);
                npgsql.EnableRetryOnFailure(maxRetryCount: 3);
            });
            options.AddInterceptors(provider.GetRequiredService<AuditableEntityInterceptor>());
        });

        return services;
    }
}
