using System.Text;
using System.Text.Json;
using Stripe;

namespace DatabaseAIAgent.Api.Tests.Billing;

/// <summary>Builds Stripe webhook payloads signed exactly as Stripe signs them (HMAC-SHA256 over "timestamp.payload").</summary>
public static class StripeWebhooks
{
    public const string Secret = "whsec_test_secret_for_signature_checks";
    public const string AccountReference = "test-deployment";

    public static string SubscriptionEvent(
        string eventId,
        string type,
        string status,
        string priceId,
        DateTimeOffset created,
        string accountReference = AccountReference,
        string subscriptionId = FakeStripeApi.SubscriptionId,
        string customerId = FakeStripeApi.CustomerId,
        bool cancelAtPeriodEnd = false,
        long? trialEnd = null) =>
        Envelope(eventId, type, created, new
        {
            id = subscriptionId,
            @object = "subscription",
            customer = customerId,
            status,
            cancel_at_period_end = cancelAtPeriodEnd,
            trial_end = trialEnd,
            metadata = new { account_reference = accountReference, plan_code = "professional" },
            items = new
            {
                @object = "list",
                data = new[]
                {
                    new
                    {
                        id = "si_test_1",
                        @object = "subscription_item",
                        current_period_start = created.ToUnixTimeSeconds(),
                        current_period_end = created.AddMonths(1).ToUnixTimeSeconds(),
                        price = new { id = priceId, @object = "price" },
                    },
                },
            },
        });

    public static string InvoicePaidEvent(string eventId, string invoiceId, long amountCents, DateTimeOffset created, string customerId = FakeStripeApi.CustomerId) =>
        Envelope(eventId, "invoice.paid", created, new
        {
            id = invoiceId,
            @object = "invoice",
            customer = customerId,
            number = "INV-0001",
            status = "paid",
            currency = "usd",
            amount_due = amountCents,
            amount_paid = amountCents,
            created = created.ToUnixTimeSeconds(),
            period_start = created.ToUnixTimeSeconds(),
            period_end = created.AddMonths(1).ToUnixTimeSeconds(),
            status_transitions = new { paid_at = created.ToUnixTimeSeconds() },
            hosted_invoice_url = "https://invoice.stripe.com/i/test",
            invoice_pdf = "javascript:alert(1)",
            parent = new { type = "subscription_details", subscription_details = new { subscription = FakeStripeApi.SubscriptionId } },
        });

    public static string Envelope(string eventId, string type, DateTimeOffset created, object dataObject) => JsonSerializer.Serialize(new
    {
        id = eventId,
        @object = "event",
        api_version = StripeConfiguration.ApiVersion,
        created = created.ToUnixTimeSeconds(),
        livemode = false,
        pending_webhooks = 1,
        type,
        data = new { @object = dataObject },
    });

    public static HttpRequestMessage SignedRequest(string payload, string secret = Secret, DateTimeOffset? signedAt = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhooks/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", EventUtility.GenerateSignatureHeader(payload, secret, signedAt?.ToUnixTimeSeconds()));
        return request;
    }
}
