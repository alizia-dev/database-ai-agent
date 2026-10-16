using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Application.Abstractions.Billing;

public sealed record PlanLimitsDto(
    int? MaxDatabaseConnections,
    int? MaxUsers,
    int? MaxConversationsPerDay,
    int? MaxApprovedTools,
    int? MaxToolExecutionsPerMonth)
{
    public static PlanLimitsDto From(PlanLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new(limits.MaxDatabaseConnections, limits.MaxUsers, limits.MaxConversationsPerDay, limits.MaxApprovedTools, limits.MaxToolExecutionsPerMonth);
    }
}

/// <summary>What the workspace may currently do, derived from its subscription (or the free plan).</summary>
public sealed record Entitlements(
    string PlanCode,
    string PlanName,
    bool IsFreePlan,
    SubscriptionStatus? SubscriptionStatus,
    bool HasProductionAccess,
    PlanLimitsDto Limits,
    IReadOnlyList<string> Features,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    bool PaymentPastDue)
{
    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.Ordinal);
}

/// <summary>
/// The single server-side source of truth for plan-based access. Every module checks entitlements here; the client
/// only uses them to adapt the UI.
/// </summary>
public interface IEntitlementService
{
    Task<Entitlements> GetCurrentAsync(CancellationToken cancellationToken);
}
