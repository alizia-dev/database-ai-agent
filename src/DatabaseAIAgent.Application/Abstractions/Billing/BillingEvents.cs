using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Application.Abstractions.Billing;

/// <summary>Provider-neutral billing event, produced from a verified webhook.</summary>
public abstract record BillingEvent(DateTimeOffset OccurredAt);

/// <summary>
/// The subscription's current state. <see cref="AccountReference"/> identifies the deployment that created it, so
/// events belonging to other deployments sharing the same gateway account are ignored.
/// </summary>
public sealed record SubscriptionChangedEvent(
    string ProviderSubscriptionId,
    string? ProviderCustomerId,
    string? AccountReference,
    string? PriceId,
    SubscriptionStatus Status,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? TrialEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? CanceledAt,
    DateTimeOffset OccurredAt) : BillingEvent(OccurredAt);

public sealed record CheckoutCompletedEvent(
    string? AccountReference,
    string? ProviderCustomerId,
    string? ProviderSubscriptionId,
    DateTimeOffset OccurredAt) : BillingEvent(OccurredAt);

public sealed record InvoiceChangedEvent(
    string ProviderInvoiceId,
    string? ProviderCustomerId,
    string? ProviderSubscriptionId,
    InvoiceSnapshot Invoice,
    DateTimeOffset OccurredAt) : BillingEvent(OccurredAt);

public sealed record PaymentRecordedEvent(
    string ProviderPaymentId,
    string? ProviderCustomerId,
    string? ProviderSubscriptionId,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    string? FailureMessage,
    DateTimeOffset OccurredAt) : BillingEvent(OccurredAt);

public sealed record PaymentRefundedEvent(
    string ProviderPaymentId,
    decimal AmountRefunded,
    DateTimeOffset OccurredAt) : BillingEvent(OccurredAt);

/// <summary>An authentic event the platform does not act on (e.g. an unrelated event type).</summary>
public sealed record IgnoredBillingEvent(string Reason, DateTimeOffset OccurredAt) : BillingEvent(OccurredAt);
