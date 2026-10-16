using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Rag;

/// <summary>Composition entry point for the Rag module. Services are registered from Phase 7 (RAG).</summary>
public static class RagModule
{
    public static IServiceCollection AddRagModule(this IServiceCollection services) => services;
}
