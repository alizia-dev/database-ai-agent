using DatabaseAIAgent.Application;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Billing;
using DatabaseAIAgent.Database;
using DatabaseAIAgent.Infrastructure;
using DatabaseAIAgent.Rag;
using DatabaseAIAgent.Security;

namespace DatabaseAIAgent.Workers;

/// <summary>
/// Service composition for the Workers host. Long-running work (schema analysis, embeddings, document processing,
/// usage aggregation, health checks, webhook processing, cleanup) runs here, never inside an HTTP request.
/// </summary>
public static class WorkerServices
{
    public static IServiceCollection AddWorkerServices(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .AddSecretProtection(configuration, environment)
            .AddBillingModule(configuration)
            .AddBillingWorkers()
            .AddDatabaseModule(configuration)
            .AddRagModule();

        // Background jobs act as the system, never as a signed-in user.
        services.AddSingleton<ICurrentUser, SystemCurrentUser>();
        return services;
    }

    private sealed class SystemCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;

        public bool IsAuthenticated => false;

        public string? Email => null;

        public string? IpAddress => null;

        public string? UserAgent => null;
    }
}
