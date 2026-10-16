using DatabaseAIAgent.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the platform database. It stores platform state and customer-database
/// <em>metadata</em> only; transactional customer data is never copied here.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("platform");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
    }
}
