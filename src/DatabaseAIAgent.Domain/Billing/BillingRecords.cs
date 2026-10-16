using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Billing;

/// <summary>An invoice issued by the payment provider (a local copy of metadata and links; no card data).</summary>
public sealed class Invoice : Entity
{
    private Invoice()
    {
    }

    public Invoice(PaymentProvider provider, string providerInvoiceId)
    {
        Provider = provider;
        ProviderInvoiceId = Text.Required(providerInvoiceId, nameof(ProviderInvoiceId), Plan.ProviderIdMaxLength);
    }

    public PaymentProvider Provider { get; private set; }

    public string ProviderInvoiceId { get; private set; } = string.Empty;

    public Guid? SubscriptionId { get; private set; }

    public string? Number { get; private set; }

    public InvoiceStatus Status { get; private set; }

    public string Currency { get; private set; } = "USD";

    public decimal AmountDue { get; private set; }

    public decimal AmountPaid { get; private set; }

    public DateTimeOffset? PeriodStart { get; private set; }

    public DateTimeOffset? PeriodEnd { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public string? HostedInvoiceUrl { get; private set; }

    public string? PdfUrl { get; private set; }

    public void Update(InvoiceSnapshot snapshot, Guid? subscriptionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SubscriptionId = subscriptionId ?? SubscriptionId;
        Number = Text.Optional(snapshot.Number, nameof(Number), 64);
        Status = snapshot.Status;
        Currency = snapshot.Currency.ToUpperInvariant();
        AmountDue = snapshot.AmountDue;
        AmountPaid = snapshot.AmountPaid;
        PeriodStart = snapshot.PeriodStart;
        PeriodEnd = snapshot.PeriodEnd;
        IssuedAt = snapshot.IssuedAt;
        PaidAt = snapshot.PaidAt ?? PaidAt;
        HostedInvoiceUrl = SafeUrl(snapshot.HostedInvoiceUrl);
        PdfUrl = SafeUrl(snapshot.PdfUrl);
    }

    // Only https links from the provider are kept, so a manipulated event cannot inject a javascript: URL into the UI.
    private static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && url!.Length <= 2048 ? url : null;
}

public sealed record InvoiceSnapshot(
    string? Number,
    InvoiceStatus Status,
    string Currency,
    decimal AmountDue,
    decimal AmountPaid,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset IssuedAt,
    DateTimeOffset? PaidAt,
    string? HostedInvoiceUrl,
    string? PdfUrl);

/// <summary>A payment attempt (successful or failed) or its refund state. Never contains card numbers.</summary>
public sealed class Payment : Entity
{
    public const int FailureMessageMaxLength = 300;

    private Payment()
    {
    }

    public Payment(PaymentProvider provider, string providerPaymentId)
    {
        Provider = provider;
        ProviderPaymentId = Text.Required(providerPaymentId, nameof(ProviderPaymentId), Plan.ProviderIdMaxLength);
    }

    public PaymentProvider Provider { get; private set; }

    public string ProviderPaymentId { get; private set; } = string.Empty;

    public Guid? InvoiceId { get; private set; }

    public PaymentStatus Status { get; private set; }

    public string Currency { get; private set; } = "USD";

    public decimal Amount { get; private set; }

    public decimal AmountRefunded { get; private set; }

    public string? FailureMessage { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public void Record(PaymentStatus status, string currency, decimal amount, string? failureMessage, DateTimeOffset occurredAt, Guid? invoiceId)
    {
        Status = status;
        Currency = currency.ToUpperInvariant();
        Amount = amount;
        FailureMessage = failureMessage is null ? null : failureMessage.Length <= FailureMessageMaxLength ? failureMessage : failureMessage[..FailureMessageMaxLength];
        OccurredAt = occurredAt;
        InvoiceId = invoiceId ?? InvoiceId;
    }

    public void RecordRefund(decimal amountRefunded)
    {
        if (amountRefunded <= 0)
        {
            return;
        }

        AmountRefunded = Math.Min(amountRefunded, Amount);
        Status = AmountRefunded >= Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
    }
}

/// <summary>
/// A received provider webhook. The unique (Provider, ProviderEventId) pair makes processing idempotent: a
/// redelivered event is recognized and never applied twice.
/// </summary>
public sealed class WebhookEvent : Entity
{
    public const int MaxAttempts = 5;

    private WebhookEvent()
    {
    }

    public WebhookEvent(PaymentProvider provider, string providerEventId, string type, string payload, DateTimeOffset receivedAt)
    {
        Provider = provider;
        ProviderEventId = Text.Required(providerEventId, nameof(ProviderEventId), Plan.ProviderIdMaxLength);
        Type = Text.Required(type, nameof(Type), 100);
        Payload = payload;
        ReceivedAt = receivedAt;
        Status = WebhookEventStatus.Received;
    }

    public PaymentProvider Provider { get; private set; }

    public string ProviderEventId { get; private set; } = string.Empty;

    public string Type { get; private set; } = string.Empty;

    /// <summary>The verified payload, kept so failed events can be reprocessed by the worker.</summary>
    public string Payload { get; private set; } = string.Empty;

    public WebhookEventStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public bool IsRetryable => Status is WebhookEventStatus.Received or WebhookEventStatus.Failed && Attempts < MaxAttempts;

    public void MarkProcessed(DateTimeOffset at, bool ignored)
    {
        Attempts++;
        Status = ignored ? WebhookEventStatus.Ignored : WebhookEventStatus.Processed;
        ProcessedAt = at;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        Status = WebhookEventStatus.Failed;
        LastError = error.Length <= 500 ? error : error[..500];
    }
}

/// <summary>Workspace-level billing identity with each provider (singleton, like the Organization).</summary>
public sealed class BillingAccount : AuditableEntity
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000002");

    private BillingAccount()
        : base(SingletonId)
    {
    }

    public static BillingAccount Create() => new();

    public string? StripeCustomerId { get; private set; }

    public string? BillingEmail { get; private set; }

    /// <summary>Set when a checkout was started; cleared when the subscription is confirmed by webhook.</summary>
    public DateTimeOffset? CheckoutStartedAt { get; private set; }

    public void LinkStripeCustomer(string customerId) =>
        StripeCustomerId = Text.Required(customerId, nameof(StripeCustomerId), Plan.ProviderIdMaxLength);

    public void SetBillingEmail(string? email) => BillingEmail = Text.Optional(email, nameof(BillingEmail), 256);

    public void MarkCheckoutStarted(DateTimeOffset at) => CheckoutStartedAt = at;

    public void ClearPendingCheckout() => CheckoutStartedAt = null;
}
