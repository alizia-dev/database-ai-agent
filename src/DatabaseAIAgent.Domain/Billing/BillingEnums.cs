namespace DatabaseAIAgent.Domain.Billing;

public enum PaymentProvider
{
    Stripe = 0,
    PayPal = 1,

    /// <summary>Assigned by an operator (e.g. a negotiated Enterprise contract invoiced outside the platform).</summary>
    Manual = 2,
}

public enum BillingInterval
{
    Monthly = 0,
    Annual = 1,
}

/// <summary>Provider-neutral subscription status (mapped from Stripe/PayPal statuses).</summary>
public enum SubscriptionStatus
{
    /// <summary>Checkout started; first payment not confirmed yet.</summary>
    Incomplete = 0,
    Trialing = 1,
    Active = 2,

    /// <summary>A renewal payment failed; access continues during a configurable grace period.</summary>
    PastDue = 3,
    Paused = 4,
    Unpaid = 5,
    Canceled = 6,
    IncompleteExpired = 7,
}

public enum InvoiceStatus
{
    Draft = 0,
    Open = 1,
    Paid = 2,
    Void = 3,
    Uncollectible = 4,
}

public enum PaymentStatus
{
    Succeeded = 0,
    Failed = 1,
    Refunded = 2,
    PartiallyRefunded = 3,
}

public enum WebhookEventStatus
{
    Received = 0,
    Processed = 1,
    Ignored = 2,
    Failed = 3,
}
