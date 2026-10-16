using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Database.Networking;
using DatabaseAIAgent.Database.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Database;

/// <summary>Composition entry point for customer database connectors (SQL Server, PostgreSQL, MySQL).</summary>
public static class DatabaseModule
{
    public static IServiceCollection AddDatabaseModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseConnectionOptions>()
            .Bind(configuration.GetSection(DatabaseConnectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ICustomerDatabaseProvider, SqlServerDatabaseProvider>();
        services.AddSingleton<ICustomerDatabaseProvider, PostgresDatabaseProvider>();
        services.AddSingleton<ICustomerDatabaseProvider, MySqlDatabaseProvider>();
        services.AddSingleton<HostAddressGuard>();

        services.AddScoped<ConnectionCredentials>();
        services.AddScoped<DatabaseConnectionService>();
        services.AddScoped<ICustomerDatabaseConnector, CustomerDatabaseConnector>();

        return services;
    }
}
