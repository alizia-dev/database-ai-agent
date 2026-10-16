using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Database;

/// <summary>Composition entry point for the Database module. Services are registered from Phase 4 (customer database connectors).</summary>
public static class DatabaseModule
{
    public static IServiceCollection AddDatabaseModule(this IServiceCollection services) => services;
}
