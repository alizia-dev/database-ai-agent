using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Billing;

/// <summary>Composition entry point for the Billing module. Services are registered from Phase 3 (billing and subscriptions).</summary>
public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services) => services;
}
