using System.Text.Json;
using System.Text.Json.Nodes;
using DatabaseAIAgent.Application.Abstractions.Billing;
using Microsoft.Extensions.Options;
using PaymentProviderKind = DatabaseAIAgent.Domain.Billing.PaymentProvider;

namespace DatabaseAIAgent.Billing.Providers.PayPal;

/// <summary>
/// PayPal (secondary provider) via the Subscriptions REST API. The customer approves the subscription on PayPal;
/// activation arrives through verified webhooks. PayPal cancels immediately (no cancel-at-period-end).
/// </summary>
internal sealed class PayPalPaymentProvider(PayPalClient client, IOptions<PayPalOptions> options) : IPaymentProvider
{
    private static readonly string[] SignatureHeaders =
        ["PAYPAL-AUTH-ALGO", "PAYPAL-CERT-URL", "PAYPAL-TRANSMISSION-ID", "PAYPAL-TRANSMISSION-SIG", "PAYPAL-TRANSMISSION-TIME"];

    public PaymentProviderKind Kind => PaymentProviderKind.PayPal;

    public bool IsEnabled => options.Value.IsConfigured;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await client.SendAsync(HttpMethod.Post, "v1/billing/subscriptions", new
        {
            plan_id = request.PriceId,
            custom_id = request.AccountReference,
            subscriber = new { email_address = request.CustomerEmail },
            application_context = ApplicationContext(request.SuccessUrl, request.CancelUrl),
        }, cancellationToken);

        return new CheckoutSession(ApprovalLink(response) ?? throw new PaymentProviderException("PayPal did not return an approval link."), ProviderCustomerId: null);
    }

    public Task<string> CreateManagementUrlAsync(ManagementRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(options.Value.ManagementUrl);

    public async Task<ProviderActionResult> ChangePlanAsync(ChangePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await client.SendAsync(HttpMethod.Post, $"v1/billing/subscriptions/{Uri.EscapeDataString(request.ProviderSubscriptionId)}/revise", new
        {
            plan_id = request.NewPriceId,
            application_context = ApplicationContext(request.ReturnUrl, request.CancelUrl),
        }, cancellationToken);

        return new ProviderActionResult(ApprovalLink(response));
    }

    public async Task CancelAsync(string providerSubscriptionId, bool atPeriodEnd, CancellationToken cancellationToken) =>
        await client.SendAsync(HttpMethod.Post, $"v1/billing/subscriptions/{Uri.EscapeDataString(providerSubscriptionId)}/cancel",
            new { reason = "Cancelled by the workspace owner." }, cancellationToken);

    public Task<bool> ResumeAsync(string providerSubscriptionId, CancellationToken cancellationToken) => Task.FromResult(false);

    public async Task RefundAsync(string providerPaymentId, decimal? amount, string currency, CancellationToken cancellationToken) =>
        await client.SendAsync(HttpMethod.Post, $"v1/payments/sale/{Uri.EscapeDataString(providerPaymentId)}/refund",
            amount is { } a ? new { amount = new { total = Money.Format(a), currency } } : new { }, cancellationToken);

    public async Task<VerifiedWebhook?> VerifyWebhookAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SignatureHeaders)
        {
            if (!request.Headers.TryGetValue(name, out var value) || string.IsNullOrEmpty(value))
            {
                return null;
            }

            headers[name] = value;
        }

        JsonNode? webhookEvent;
        try
        {
            webhookEvent = JsonNode.Parse(request.Payload);
        }
        catch (JsonException)
        {
            return null;
        }

        var eventId = webhookEvent?["id"]?.GetValue<string>();
        var eventType = webhookEvent?["event_type"]?.GetValue<string>();
        if (eventId is null || eventType is null)
        {
            return null;
        }

        // PayPal verifies the transmission signature against its certificate and this deployment's webhook ID.
        var verification = await client.SendAsync(HttpMethod.Post, "v1/notifications/verify-webhook-signature", new JsonObject
        {
            ["auth_algo"] = headers["PAYPAL-AUTH-ALGO"],
            ["cert_url"] = headers["PAYPAL-CERT-URL"],
            ["transmission_id"] = headers["PAYPAL-TRANSMISSION-ID"],
            ["transmission_sig"] = headers["PAYPAL-TRANSMISSION-SIG"],
            ["transmission_time"] = headers["PAYPAL-TRANSMISSION-TIME"],
            ["webhook_id"] = options.Value.WebhookId,
            ["webhook_event"] = webhookEvent,
        }, cancellationToken);

        return verification?["verification_status"]?.GetValue<string>() == "SUCCESS"
            ? new VerifiedWebhook(eventId, eventType)
            : null;
    }

    public BillingEvent Interpret(string payload) => PayPalEventTranslator.Translate(payload);

    private object ApplicationContext(string returnUrl, string cancelUrl) => new
    {
        brand_name = options.Value.BrandName,
        user_action = "SUBSCRIBE_NOW",
        shipping_preference = "NO_SHIPPING",
        return_url = returnUrl,
        cancel_url = cancelUrl,
    };

    private static string? ApprovalLink(JsonNode? response) =>
        response?["links"]?.AsArray()
            .FirstOrDefault(link => link?["rel"]?.GetValue<string>() == "approve")?["href"]?.GetValue<string>();
}
