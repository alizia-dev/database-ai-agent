using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Database.Discovery;
using DatabaseAIAgent.Database.Execution;
using DatabaseAIAgent.Database.Networking;
using DatabaseAIAgent.Database.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Database;

/// <summary>Composition entry point for customer database connectors and schema discovery.</summary>
public static class DatabaseModule
{
    public static IServiceCollection AddDatabaseModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseConnectionOptions>()
            .Bind(configuration.GetSection(DatabaseConnectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DiscoveryOptions>()
            .Bind(configuration.GetSection(DiscoveryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<ICustomerDatabaseProvider, SqlServerDatabaseProvider>();
        services.AddSingleton<ICustomerDatabaseProvider, PostgresDatabaseProvider>();
        services.AddSingleton<ICustomerDatabaseProvider, MySqlDatabaseProvider>();
        services.AddSingleton<ISchemaDiscoverer, SqlServerSchemaDiscoverer>();
        services.AddSingleton<ISchemaDiscoverer, PostgresSchemaDiscoverer>();
        services.AddSingleton<ISchemaDiscoverer, MySqlSchemaDiscoverer>();
        services.AddSingleton<HostAddressGuard>();

        services.AddScoped<ConnectionCredentials>();
        services.AddScoped<DatabaseConnectionService>();
        services.AddScoped<ICustomerDatabaseConnector, CustomerDatabaseConnector>();

        services.AddScoped<SchemaCatalogWriter>();
        services.AddScoped<SchemaAnalysisService>();
        services.AddScoped<IJobHandler, SchemaAnalysisJobHandler>();

        services.AddOptions<ExecutionOptions>().Bind(configuration.GetSection(ExecutionOptions.SectionName));
        services.AddSingleton<ConnectionGates>();
        services.AddScoped<ICustomerProcedureInvoker, CustomerProcedureInvoker>();

        return services;
    }
}
