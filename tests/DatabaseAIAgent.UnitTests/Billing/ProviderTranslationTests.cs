using System.Net;
using System.Text;
using System.Text.Json;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Billing;
using DatabaseAIAgent.Billing.Providers;
using DatabaseAIAgent.Billing.Providers.PayPal;
using DatabaseAIAgent.Billing.Providers.Stripe;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;

namespace DatabaseAIAgent.UnitTests.Billing;

public sealed class ProviderTranslationTests
{
    private const long Created = 1767225600; // 2026-01-01T00:00:00Z

    [Fact]
    public void Stripe_SubscriptionEvent_MapsStatusPriceAndPeriod()
    {
        var billingEvent = Translate("customer.subscription.updated", new
        {
            id = "sub_1",
            @object = "subscription",
            customer = "cus_1",
            status = "past_due",
            cancel_at_period_end = false,
            cancel_at = Created + 100,
            metadata = new { account_reference = "acme" },
            items = new { @object = "list", data = new[] { new { id = "si_1", @object = "subscription_item", current_period_start = Created, current_period_end = Created + 2_678_400, price = new { id = "price_1", @object = "price" } } } },
        });

        var e = Assert.IsType<SubscriptionChangedEvent>(billingEvent);
        Assert.Equal(SubscriptionStatus.PastDue, e.Status);
        Assert.Equal("price_1", e.PriceId);
        Assert.Equal("acme", e.AccountReference);
        Assert.True(e.CancelAtPeriodEnd); // portal-style cancel_at counts as scheduled cancellation
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Created + 2_678_400), e.CurrentPeriodEnd);
    }

    [Fact]
    public void Stripe_PaymentIntentAndRefundEvents_ConvertMinorUnits()
    {
        var failed = Assert.IsType<PaymentRecordedEvent>(Translate("payment_intent.payment_failed", new
        {
            id = "pi_1",
            @object = "payment_intent",
            amount = 14900,
            currency = "usd",
            customer = "cus_1",
            status = "requires_payment_method",
            last_payment_error = new { message = "Your card was declined." },
        }));
        var refund = Assert.IsType<PaymentRefundedEvent>(Translate("charge.refunded", new
        {
            id = "ch_1",
            @object = "charge",
            amount = 5000,
            amount_refunded = 5000,
            currency = "jpy",
            payment_intent = "pi_2",
        }));

        Assert.Equal(PaymentStatus.Failed, failed.Status);
        Assert.Equal(149m, failed.Amount);
        Assert.Equal("Your card was declined.", failed.FailureMessage);
        Assert.Equal(5000m, refund.AmountRefunded); // JPY has no minor unit
    }

    [Fact]
    public void Stripe_UnrelatedEvent_IsIgnored() =>
        Assert.IsType<IgnoredBillingEvent>(Translate("customer.created", new { id = "cus_1", @object = "customer" }));

    [Fact]
    public void Stripe_SignatureVerification_AcceptsOnlyCorrectlySignedRecentPayloads()
    {
        const string secret = "whsec_unit";
        var provider = new StripePaymentProvider(
            new StripeClient("sk_test_unit"),
            Options.Create(new StripeOptions { SecretKey = "sk_test_unit", WebhookSecret = secret }),
            NullLogger<StripePaymentProvider>.Instance);
        var payload = Envelope("customer.created", new { id = "cus_1", @object = "customer" });

        VerifiedWebhook? Verify(string header) =>
            provider.VerifyWebhookAsync(new WebhookRequest(payload, new Dictionary<string, string> { ["Stripe-Signature"] = header }), CancellationToken.None).Result;

        Assert.NotNull(Verify(EventUtility.GenerateSignatureHeader(payload, secret, null)));
        Assert.Null(Verify(EventUtility.GenerateSignatureHeader(payload, "whsec_other", null)));
        Assert.Null(Verify(EventUtility.GenerateSignatureHeader(payload, secret, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds())));
        Assert.Null(Verify("t=1,v1=deadbeef"));
    }

    [Fact]
    public void PayPal_SubscriptionEvents_MapStatusesAndFailedPayments()
    {
        static SubscriptionChangedEvent Map(string status, int failed) => Assert.IsType<SubscriptionChangedEvent>(PayPalEventTranslator.Translate(JsonSerializer.Serialize(new
        {
            id = "WH-1",
            event_type = "BILLING.SUBSCRIPTION.UPDATED",
            create_time = "2026-01-01T00:00:00Z",
            resource = new
            {
                id = "I-SUB1",
                status,
                plan_id = "P-PLAN1",
                custom_id = "acme",
                subscriber = new { payer_id = "PAYER1" },
                billing_info = new { next_billing_time = "2026-02-01T00:00:00Z", failed_payments_count = failed },
                update_time = "2026-01-01T00:00:00Z",
            },
        })));

        Assert.Equal(SubscriptionStatus.Active, Map("ACTIVE", 0).Status);
        Assert.Equal(SubscriptionStatus.PastDue, Map("ACTIVE", 1).Status);
        Assert.Equal(SubscriptionStatus.PastDue, Map("SUSPENDED", 0).Status);
        Assert.Equal(SubscriptionStatus.Canceled, Map("CANCELLED", 0).Status);
        Assert.Equal(SubscriptionStatus.Incomplete, Map("APPROVAL_PENDING", 0).Status);
        Assert.Equal("acme", Map("ACTIVE", 0).AccountReference);
        Assert.Equal("P-PLAN1", Map("ACTIVE", 0).PriceId);
    }

    [Fact]
    public void PayPal_SaleEvents_MapToPaymentsAndRefunds()
    {
        var sale = Assert.IsType<PaymentRecordedEvent>(PayPalEventTranslator.Translate("""
            {"id":"WH-2","event_type":"PAYMENT.SALE.COMPLETED","create_time":"2026-01-01T00:00:00Z",
             "resource":{"id":"SALE1","amount":{"total":"149.00","currency":"USD"},"billing_agreement_id":"I-SUB1","create_time":"2026-01-01T00:00:00Z"}}
            """));
        var refund = Assert.IsType<PaymentRefundedEvent>(PayPalEventTranslator.Translate("""
            {"id":"WH-3","event_type":"PAYMENT.SALE.REFUNDED","create_time":"2026-01-02T00:00:00Z",
             "resource":{"id":"REF1","sale_id":"SALE1","amount":{"total":"49.00","currency":"USD"}}}
            """));

        Assert.Equal(149m, sale.Amount);
        Assert.Equal("I-SUB1", sale.ProviderSubscriptionId);
        Assert.Equal("SALE1", refund.ProviderPaymentId);
        Assert.Equal(49m, refund.AmountRefunded);
    }

    [Theory]
    [InlineData("SUCCESS", true)]
    [InlineData("FAILURE", false)]
    public async Task PayPal_WebhookVerification_AsksPayPalWithTheConfiguredWebhookId(string verificationStatus, bool accepted)
    {
        PayPalClient.ResetTokenCache();
        var handler = new StubPayPal(verificationStatus);
        var options = Options.Create(new PayPalOptions { ClientId = "id", ClientSecret = "secret", WebhookId = "WH-CONFIG" });
        var provider = new PayPalPaymentProvider(new PayPalClient(new HttpClient(handler), options, TimeProvider.System, NullLogger<PayPalClient>.Instance), options);
        var headers = new Dictionary<string, string>
        {
            ["PAYPAL-AUTH-ALGO"] = "SHA256withRSA",
            ["PAYPAL-CERT-URL"] = "https://api.paypal.com/cert",
            ["PAYPAL-TRANSMISSION-ID"] = "t-1",
            ["PAYPAL-TRANSMISSION-SIG"] = "sig",
            ["PAYPAL-TRANSMISSION-TIME"] = "2026-01-01T00:00:00Z",
        };

        var verified = await provider.VerifyWebhookAsync(new WebhookRequest("""{"id":"WH-9","event_type":"PAYMENT.SALE.COMPLETED","resource":{}}""", headers), CancellationToken.None);
        var missingHeaders = await provider.VerifyWebhookAsync(new WebhookRequest("""{"id":"WH-9","event_type":"X"}""", new Dictionary<string, string>()), CancellationToken.None);

        Assert.Equal(accepted, verified is not null);
        Assert.Null(missingHeaders);
        Assert.Contains("\"webhook_id\":\"WH-CONFIG\"", handler.VerificationBody, StringComparison.Ordinal);
        Assert.Contains("\"transmission_id\":\"t-1\"", handler.VerificationBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(14900, "usd", 149)]
    [InlineData(1000, "JPY", 1000)]
    public void Money_ConvertsMinorUnits(long minor, string currency, decimal expected)
    {
        Assert.Equal(expected, Money.FromMinorUnits(minor, currency));
        Assert.Equal(minor, Money.ToMinorUnits(expected, currency));
    }

    private static BillingEvent Translate(string type, object dataObject) =>
        StripeEventTranslator.Translate(EventUtility.ParseEvent(Envelope(type, dataObject), throwOnApiVersionMismatch: false));

    private static string Envelope(string type, object dataObject) => JsonSerializer.Serialize(new
    {
        id = "evt_1",
        @object = "event",
        api_version = StripeConfiguration.ApiVersion,
        created = Created,
        type,
        data = new { @object = dataObject },
    });

    private sealed class StubPayPal(string verificationStatus) : HttpMessageHandler
    {
        public string VerificationBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/oauth2/token")
            {
                return Json("""{"access_token":"token-1","expires_in":3600}""");
            }

            if (path == "/v1/notifications/verify-webhook-signature")
            {
                Assert.Equal("Bearer token-1", request.Headers.Authorization?.ToString());
                VerificationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json($$"""{"verification_status":"{{verificationStatus}}"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
