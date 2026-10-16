using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Billing.Subscriptions;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.AspNetCore.Http;

namespace DatabaseAIAgent.Api.Tests.Databases;

/// <summary>
/// Test-only decorator: requests carrying <see cref="Header"/> get a paid plan (optionally with a connection limit),
/// so module tests can run independently of the shared billing state. Without the header the real service is used.
/// </summary>
internal sealed class TestEntitlements(EntitlementService real, IHttpContextAccessor http) : IEntitlementService
{
    public const string Header = "X-Test-Paid-Plan";

    public async Task<Entitlements> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var value = http.HttpContext?.Request.Headers[Header].ToString();
        if (string.IsNullOrEmpty(value))
        {
            return await real.GetCurrentAsync(cancellationToken);
        }

        int? maxConnections = int.TryParse(value, out var max) ? max : null;
        return new Entitlements(
            "professional", "Professional", false, SubscriptionStatus.Active, true,
            new PlanLimitsDto(maxConnections, 15, 200, 100, 100_000),
            [PlanFeatures.ProductionAccess, PlanFeatures.Rag], null, null, false, false);
    }
}
