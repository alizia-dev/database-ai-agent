using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Agent;

/// <summary>Composition entry point for the Agent module. Services are registered from Phase 9 (agent and tool calling).</summary>
public static class AgentModule
{
    public static IServiceCollection AddAgentModule(this IServiceCollection services) => services;
}
