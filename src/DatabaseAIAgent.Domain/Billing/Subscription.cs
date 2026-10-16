using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Billing;

/// <summary>
/// The workspace's subscription with a payment provider. State changes come only from verified provider events
/// (webhooks) or an operator, never from a browser redirect.
/// </summary>
public sealed class Subscription : AuditableEntity
{
    private Subscription()
    {
    }

    public Subscription(Guid planId, PaymentProvider provider, string providerSubscriptionId, string? providerCustomerId, BillingInterval interval)
    {
        PlanId = planId;
        Provider = provider;
        ProviderSubscriptionId = Text.Required(providerSubscriptionId, nameof(ProviderSubscriptionId), Plan.ProviderIdMaxLength);
        ProviderCustomerId = Text.Optional(providerCustomerId, nameof(ProviderCustomerId), Plan.ProviderIdMaxLength);
        Interval = interval;
        Status = SubscriptionStatus.Incomplete;
    }

    public Guid PlanId { get; private set; }

    public PaymentProvider Provider { get; private set; }

    public string ProviderSubscriptionId { get; private set; } = string.Empty;

    public string? ProviderCustomerId { get; private set; }

    public BillingInterval Interval { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset? CurrentPeriodStart { get; private set; }

    public DateTimeOffset? CurrentPeriodEnd { get; private set; }

    public DateTimeOffset? TrialEnd { get; private set; }

    public bool CancelAtPeriodEnd { get; private set; }

    public DateTimeOffset? CanceledAt { get; private set; }

    /// <summary>When the status first became <see cref="SubscriptionStatus.PastDue"/> (starts the grace period).</summary>
    public DateTimeOffset? PastDueSince { get; private set; }

    /// <summary>Timestamp of the provider event last applied; older (out-of-order) events are ignored.</summary>
    public DateTimeOffset? LastProviderEventAt { get; private set; }

    public bool IsTerminal => Status is SubscriptionStatus.Canceled or SubscriptionStatus.IncompleteExpired;

    /// <summary>
    /// Applies a provider snapshot. Returns false (and changes nothing) when the snapshot is older than the last one
    /// applied, so delayed or redelivered events cannot roll the subscription back.
    /// </summary>
    public bool Apply(SubscriptionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (LastProviderEventAt is { } last && snapshot.OccurredAt < last)
        {
            return false;
        }

        if (Status != SubscriptionStatus.PastDue && snapshot.Status == SubscriptionStatus.PastDue)
        {
            PastDueSince = snapshot.OccurredAt;
        }
        else if (snapshot.Status != SubscriptionStatus.PastDue)
        {
            PastDueSince = null;
        }

        PlanId = snapshot.PlanId;
        Interval = snapshot.Interval;
        Status = snapshot.Status;
        ProviderCustomerId = snapshot.ProviderCustomerId ?? ProviderCustomerId;
        CurrentPeriodStart = snapshot.CurrentPeriodStart ?? CurrentPeriodStart;
        CurrentPeriodEnd = snapshot.CurrentPeriodEnd ?? CurrentPeriodEnd;
        TrialEnd = snapshot.TrialEnd;
        CancelAtPeriodEnd = snapshot.CancelAtPeriodEnd;
        CanceledAt = snapshot.CanceledAt ?? CanceledAt;
        LastProviderEventAt = snapshot.OccurredAt;
        return true;
    }

    /// <summary>Whether the subscription currently grants its plan's entitlements.</summary>
    public bool GrantsAccess(DateTimeOffset now, TimeSpan pastDueGracePeriod) => Status switch
    {
        SubscriptionStatus.Active or SubscriptionStatus.Trialing => true,
        SubscriptionStatus.PastDue => PastDueSince is null || now < PastDueSince.Value + pastDueGracePeriod,
        _ => false,
    };
}

/// <summary>The provider's view of a subscription at a point in time, normalized.</summary>
public sealed record SubscriptionSnapshot(
    Guid PlanId,
    BillingInterval Interval,
    SubscriptionStatus Status,
    string? ProviderCustomerId,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? TrialEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? CanceledAt,
    DateTimeOffset OccurredAt);
