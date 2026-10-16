using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Billing.Subscriptions;

/// <summary>
/// Derives entitlements from the current subscription. Without a subscription that grants access (none, canceled,
/// unpaid, or past due beyond the grace period) the workspace falls back to the free plan.
/// </summary>
internal sealed class EntitlementService(
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IClock clock,
    IOptions<BillingOptions> options) : IEntitlementService
{
    private Entitlements? _cached;

    public async Task<Entitlements> GetCurrentAsync(CancellationToken cancellationToken)
    {
        // Scoped: computed once per request.
        if (_cached is not null)
        {
            return _cached;
        }

        var subscription = await subscriptions.GetCurrentAsync(cancellationToken);
        var grace = TimeSpan.FromDays(options.Value.PastDueGraceDays);

        if (subscription is not null && subscription.GrantsAccess(clock.UtcNow, grace)
            && await plans.FindAsync(subscription.PlanId, cancellationToken) is { } paidPlan)
        {
            return _cached = Build(paidPlan, subscription);
        }

        var free = (await plans.ListAsync(cancellationToken)).FirstOrDefault(p => p.IsFree && p.IsActive);
        return _cached = free is null
            ? new Entitlements("none", "No plan", true, subscription?.Status, false, PlanLimitsDto.From(PlanLimits.None), [], null, null, false, false)
            : Build(free, subscription is { IsTerminal: false } ? subscription : null);
    }

    private static Entitlements Build(Plan plan, Subscription? subscription) => new(
        plan.Code,
        plan.Name,
        plan.IsFree,
        subscription?.Status,
        plan.HasFeature(PlanFeatures.ProductionAccess),
        PlanLimitsDto.From(plan.Limits),
        plan.Features,
        subscription?.Status == SubscriptionStatus.Trialing ? subscription.TrialEnd : null,
        subscription?.CurrentPeriodEnd,
        subscription?.CancelAtPeriodEnd ?? false,
        subscription?.Status == SubscriptionStatus.PastDue);
}
