using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Tools;

/// <summary>Composition entry point for the Tools module. Services are registered from Phase 6 (tool registry and generation).</summary>
public static class ToolsModule
{
    public static IServiceCollection AddToolsModule(this IServiceCollection services) => services;
}
