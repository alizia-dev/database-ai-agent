using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Tools.Execution;
using DatabaseAIAgent.Tools.Generation;
using DatabaseAIAgent.Tools.Management;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Tools;

/// <summary>Composition entry point for the AI tool registry: generation, approval and execution.</summary>
public static class ToolsModule
{
    public static IServiceCollection AddToolsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddToolCatalogSync(configuration);
        services.AddScoped<IToolExecutor, ToolExecutor>();
        services.AddScoped<ToolManagementService>();
        return services;
    }

    /// <summary>Only tool generation/synchronization (used by the worker host, which runs schema analysis).</summary>
    public static IServiceCollection AddToolCatalogSync(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ToolsOptions>()
            .Bind(configuration.GetSection(ToolsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IToolCatalogSynchronizer, ToolCatalogSynchronizer>();
        return services;
    }
}
