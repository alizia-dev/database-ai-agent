using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Application.Abstractions.Billing;

/// <summary>
/// A payment gateway (Stripe, PayPal). Providers only talk to the gateway and translate its webhooks into
/// provider-neutral <see cref="BillingEvent"/>s; all state changes are applied by the billing module.
/// Card data never passes through the platform: checkout and payment-method management are hosted by the provider.
/// </summary>
public interface IPaymentProvider
{
    PaymentProvider Kind { get; }

    /// <summary>True when credentials are configured for this deployment.</summary>
    bool IsEnabled { get; }

    Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken);

    /// <summary>A provider-hosted page where the customer manages payment methods, invoices and cancellation.</summary>
    Task<string> CreateManagementUrlAsync(ManagementRequest request, CancellationToken cancellationToken);

    /// <summary>Switches the subscription to another price/plan. A redirect URL is returned when the customer must approve.</summary>
    Task<ProviderActionResult> ChangePlanAsync(ChangePlanRequest request, CancellationToken cancellationToken);

    Task CancelAsync(string providerSubscriptionId, bool atPeriodEnd, CancellationToken cancellationToken);

    /// <summary>Withdraws a pending cancel-at-period-end. Providers that cancel immediately return false.</summary>
    Task<bool> ResumeAsync(string providerSubscriptionId, CancellationToken cancellationToken);

    /// <summary>Operator tooling only; customers request refunds through support.</summary>
    Task RefundAsync(string providerPaymentId, decimal? amount, string currency, CancellationToken cancellationToken);

    /// <summary>Verifies the webhook signature. Returns null when the request is not authentic.</summary>
    Task<VerifiedWebhook?> VerifyWebhookAsync(WebhookRequest request, CancellationToken cancellationToken);

    /// <summary>Translates an already-verified payload into a billing event (pure; no network calls).</summary>
    BillingEvent Interpret(string payload);
}

public sealed record CheckoutRequest(
    string PlanCode,
    string PriceId,
    BillingInterval Interval,
    string AccountReference,
    string? ExistingCustomerId,
    string CustomerEmail,
    int? TrialDays,
    bool TrialRequiresPaymentMethod,
    string SuccessUrl,
    string CancelUrl);

public sealed record CheckoutSession(string RedirectUrl, string? ProviderCustomerId);

public sealed record ManagementRequest(string? ProviderCustomerId, string? ProviderSubscriptionId, string ReturnUrl);

public sealed record ChangePlanRequest(string ProviderSubscriptionId, string PlanCode, string NewPriceId, string ReturnUrl, string CancelUrl);

public sealed record ProviderActionResult(string? ApprovalUrl);

public sealed record WebhookRequest(string Payload, IReadOnlyDictionary<string, string> Headers);

public sealed record VerifiedWebhook(string EventId, string Type);

/// <summary>A gateway call failed. The message is safe to show; details are logged by the provider.</summary>
public sealed class PaymentProviderException(string message, Exception? innerException = null) : Exception(message, innerException)
{
    public PaymentProviderException()
        : this("The payment provider could not complete the request.")
    {
    }

    public PaymentProviderException(string message)
        : this(message, null)
    {
    }
}
