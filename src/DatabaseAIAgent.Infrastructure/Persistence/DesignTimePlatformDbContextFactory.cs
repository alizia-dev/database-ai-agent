using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DatabaseAIAgent.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> tooling. Reads <c>POSTGRES_CONNECTION_STRING</c>; the fallback points at the
/// local docker-compose database and is never used at runtime.
/// </summary>
internal sealed class DesignTimePlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=database_ai_agent;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "platform"))
            .Options;

        return new PlatformDbContext(options);
    }
}
