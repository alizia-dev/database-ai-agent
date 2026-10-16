using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Security;

/// <summary>Composition entry point for the Security module. Services are registered from Phase 2 (authentication, users, roles and permissions).</summary>
public static class SecurityModule
{
    public static IServiceCollection AddSecurityModule(this IServiceCollection services) => services;
}
