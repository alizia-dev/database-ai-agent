using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Organizations;
using DatabaseAIAgent.Domain.Users;
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
        await SeedRolesAsync(db, logger, cancellationToken);
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

    /// <summary>
    /// Creates any missing system role together with its default permissions. Existing roles keep their
    /// (possibly customized) permissions; Owner is re-synchronized to the full catalogue so new permissions
    /// introduced by upgrades are always granted to Owner.
    /// </summary>
    internal static async Task SeedRolesAsync(PlatformDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var existing = await db.Roles.ToListAsync(cancellationToken);
        foreach (var roleName in SystemRoles.All)
        {
            var role = existing.SingleOrDefault(r => r.Name == roleName);
            if (role is null)
            {
                role = new Role(roleName);
                db.Roles.Add(role);
                db.RolePermissions.AddRange(SystemRoles.DefaultPermissions[roleName].Select(p => new RolePermission(role.Id, p)));
                logger.LogInformation("Seeded role {Role}", roleName);
            }
            else if (roleName == SystemRoles.Owner)
            {
                var granted = await db.RolePermissions.Where(rp => rp.RoleId == role.Id).Select(rp => rp.Permission).ToListAsync(cancellationToken);
                db.RolePermissions.AddRange(Permissions.All.Except(granted, StringComparer.Ordinal).Select(p => new RolePermission(role.Id, p)));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
