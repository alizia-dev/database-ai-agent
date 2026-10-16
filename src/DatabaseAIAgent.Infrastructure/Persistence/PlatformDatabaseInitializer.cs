using DatabaseAIAgent.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Infrastructure.Persistence;

/// <summary>
/// Applies migrations (when enabled) and seeds required reference data. Seeding is idempotent:
/// it only inserts rows that do not yet exist and never overwrites administrator changes.
/// </summary>
public static class PlatformDatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<PlatformDatabaseOptions>>().Value;
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PlatformDatabaseInitializer));

        if (options.ApplyMigrationsOnStartup)
        {
            logger.LogInformation("Applying platform database migrations");
            await db.Database.MigrateAsync(cancellationToken);
        }

        await SeedOrganizationAsync(db, logger, cancellationToken);
    }

    internal static async Task SeedOrganizationAsync(PlatformDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        if (await db.Organizations.AnyAsync(o => o.Id == Organization.SingletonId, cancellationToken))
        {
            return;
        }

        db.Organizations.Add(Organization.CreateDefault());
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded default workspace settings");
    }
}
