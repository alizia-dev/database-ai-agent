using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Api.Tests.Billing;

public sealed class BillingFlowTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);
    private readonly DateTimeOffset _t0 = DateTimeOffset.UtcNow.AddHours(-1);

    /// <summary>
    /// The whole lifecycle against one workspace, in order (billing state is a singleton per deployment):
    /// free → checkout (no access from the redirect alone) → webhook activates → duplicate/out-of-order events are
    /// harmless → plan change → cancel at period end → past due (grace) → canceled (back to free).
    /// </summary>
    [Fact]
    public async Task SubscriptionLifecycle_IsDrivenOnlyByVerifiedWebhooks()
    {
        var owner = await _api.OwnerAsync();
        var viewer = await _api.MemberAsync(SystemRoles.Viewer);

        // 1. Free plan: no production access; feature-gated endpoints explain that an upgrade is needed.
        var entitlements = await Json(await _api.GetAsync("/api/billing/entitlements", owner.AccessToken));
        Assert.Equal("free", entitlements.GetProperty("planCode").GetString());
        Assert.False(entitlements.GetProperty("hasProductionAccess").GetBoolean());
        var gated = await _api.GetAsync("/api/__test/production-feature", owner.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, gated.StatusCode);
        Assert.Equal("billing.upgrade_required", await TestApi.ErrorCodeAsync(gated));

        // 2. Only billing managers can start checkout.
        Assert.Equal(HttpStatusCode.Forbidden, (await Checkout(viewer.AccessToken, "professional")).StatusCode);

        // 3. Checkout redirects to Stripe-hosted checkout with this deployment's reference and a first-time trial.
        var checkout = await Checkout(owner.AccessToken, "professional");
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        Assert.StartsWith("https://checkout.stripe.com/", (await Json(checkout)).GetProperty("url").GetString(), StringComparison.Ordinal);
        var session = factory.Stripe.Requests.Last(r => r.Path == "/v1/checkout/sessions");
        Assert.Contains($"client_reference_id={StripeWebhooks.AccountReference}", session.Body, StringComparison.Ordinal);
        Assert.Contains("line_items[0][price]=price_pro_monthly", session.Body, StringComparison.Ordinal);
        Assert.Contains("subscription_data[trial_period_days]=7", session.Body, StringComparison.Ordinal);
        Assert.Contains($"customer={FakeStripeApi.CustomerId}", session.Body, StringComparison.Ordinal);

        // 4. Returning from checkout grants nothing by itself: still free, shown as "payment processing".
        var pending = await Json(await _api.GetAsync("/api/billing/subscription", owner.AccessToken));
        Assert.True(pending.GetProperty("checkoutPending").GetBoolean());
        Assert.Equal("free", pending.GetProperty("entitlements").GetProperty("planCode").GetString());

        // 5. The signed webhook activates the subscription.
        var created = StripeWebhooks.SubscriptionEvent("evt_life_1", "customer.subscription.created", "trialing", "price_pro_monthly", _t0.AddSeconds(10), trialEnd: _t0.AddDays(7).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.OK, (await Webhook(created)).StatusCode);
        var active = await Json(await _api.GetAsync("/api/billing/subscription", owner.AccessToken));
        Assert.Equal("professional", active.GetProperty("entitlements").GetProperty("planCode").GetString());
        Assert.Equal("Trialing", active.GetProperty("subscription").GetProperty("status").GetString());
        Assert.False(active.GetProperty("checkoutPending").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/__test/production-feature", owner.AccessToken)).StatusCode);

        // 6. Redelivery of the same event is acknowledged but stored and applied once.
        Assert.Equal(HttpStatusCode.OK, (await Webhook(created)).StatusCode);
        Assert.Equal(1, await CountAsync(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == "evt_life_1")));
        Assert.Equal(1, await CountAsync(db => db.Subscriptions.CountAsync()));

        // 7. Invoices are recorded; only https links from the provider are kept.
        Assert.Equal(HttpStatusCode.OK, (await Webhook(StripeWebhooks.InvoicePaidEvent("evt_life_inv", "in_test_1", 14900, _t0.AddSeconds(20)))).StatusCode);
        var invoice = (await Json(await _api.GetAsync("/api/billing/history", owner.AccessToken))).GetProperty("invoices")[0];
        Assert.Equal(149m, invoice.GetProperty("amountPaid").GetDecimal());
        Assert.Equal("https://invoice.stripe.com/i/test", invoice.GetProperty("hostedInvoiceUrl").GetString());
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("pdfUrl").ValueKind);

        // 8. An older event delivered late cannot roll the subscription back.
        var stale = StripeWebhooks.SubscriptionEvent("evt_life_stale", "customer.subscription.updated", "canceled", "price_pro_monthly", _t0.AddSeconds(5));
        Assert.Equal(HttpStatusCode.OK, (await Webhook(stale)).StatusCode);
        Assert.Equal("Trialing", (await Json(await _api.GetAsync("/api/billing/subscription", owner.AccessToken))).GetProperty("subscription").GetProperty("status").GetString());

        // 9. A second checkout is refused; plan changes go through the provider and are confirmed by webhook.
        Assert.Equal(HttpStatusCode.Conflict, (await Checkout(owner.AccessToken, "business")).StatusCode);
        var change = await _api.SendAsync(HttpMethod.Post, "/api/billing/change-plan", owner.AccessToken, new { planCode = "business", interval = "Monthly" });
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal("processing", (await Json(change)).GetProperty("status").GetString());
        Assert.Contains(factory.Stripe.Requests, r => r.Method == "POST" && r.Path == $"/v1/subscriptions/{FakeStripeApi.SubscriptionId}" && r.Body.Contains("items[0][price]=price_business_monthly", StringComparison.Ordinal));
        await Webhook(StripeWebhooks.SubscriptionEvent("evt_life_2", "customer.subscription.updated", "active", "price_business_monthly", _t0.AddSeconds(30)));
        Assert.Equal("business", (await Json(await _api.GetAsync("/api/billing/entitlements", owner.AccessToken))).GetProperty("planCode").GetString());

        // 10. Cancel at period end keeps access until then and can be undone.
        Assert.Equal(HttpStatusCode.NoContent, (await _api.SendAsync(HttpMethod.Post, "/api/billing/cancel", owner.AccessToken, new { atPeriodEnd = true })).StatusCode);
        Assert.Contains(factory.Stripe.Requests, r => r.Body.Contains("cancel_at_period_end=true", StringComparison.Ordinal));
        await Webhook(StripeWebhooks.SubscriptionEvent("evt_life_3", "customer.subscription.updated", "active", "price_business_monthly", _t0.AddSeconds(40), cancelAtPeriodEnd: true));
        var canceling = (await Json(await _api.GetAsync("/api/billing/subscription", owner.AccessToken))).GetProperty("subscription");
        Assert.True(canceling.GetProperty("cancelAtPeriodEnd").GetBoolean());
        Assert.True(canceling.GetProperty("canResume").GetBoolean());

        // 11. A failed renewal keeps access during the grace period and is flagged.
        await Webhook(StripeWebhooks.SubscriptionEvent("evt_life_4", "customer.subscription.updated", "past_due", "price_business_monthly", _t0.AddSeconds(50)));
        var pastDue = await Json(await _api.GetAsync("/api/billing/entitlements", owner.AccessToken));
        Assert.True(pastDue.GetProperty("paymentPastDue").GetBoolean());
        Assert.True(pastDue.GetProperty("hasProductionAccess").GetBoolean());

        // 12. The provider-hosted portal is available for payment methods and invoices.
        var portal = await _api.SendAsync(HttpMethod.Post, "/api/billing/portal", owner.AccessToken);
        Assert.StartsWith("https://billing.stripe.com/", (await Json(portal)).GetProperty("url").GetString(), StringComparison.Ordinal);

        // 13. When the subscription ends, the workspace falls back to the free plan.
        await Webhook(StripeWebhooks.SubscriptionEvent("evt_life_5", "customer.subscription.deleted", "canceled", "price_business_monthly", _t0.AddSeconds(60)));
        Assert.Equal("free", (await Json(await _api.GetAsync("/api/billing/entitlements", owner.AccessToken))).GetProperty("planCode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/__test/production-feature", owner.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Plans_ArePublicAndComeFromTheCatalogue()
    {
        var response = await _api.Client.GetAsync("/api/billing/plans", TestContext.Current.CancellationToken);
        var plans = (await Json(response)).GetProperty("plans").EnumerateArray().ToDictionary(p => p.GetProperty("code").GetString()!);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["free", "starter", "professional", "business", "enterprise"], plans.Keys.ToArray());
        Assert.Equal(149m, plans["professional"].GetProperty("monthlyPrice").GetDecimal());
        Assert.Equal(20, plans["professional"].GetProperty("annualSavingsPercent").GetInt32());
        Assert.Contains("Stripe", plans["professional"].GetProperty("checkoutProviders").EnumerateArray().Select(p => p.GetString()));
        Assert.Empty(plans["starter"].GetProperty("checkoutProviders").EnumerateArray()); // no price ID configured in tests
        Assert.True(plans["enterprise"].GetProperty("isCustomPricing").GetBoolean());
        Assert.DoesNotContain("stripeMonthlyPriceId", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Webhook_RejectsMissingWrongOrStaleSignatures()
    {
        var payload = StripeWebhooks.SubscriptionEvent("evt_sig_1", "customer.subscription.updated", "active", "price_pro_monthly", _t0);

        using var unsigned = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhooks/stripe") { Content = new StringContent(payload) };
        var wrongSecret = await _api.Client.SendAsync(StripeWebhooks.SignedRequest(payload, secret: "whsec_attacker"), TestContext.Current.CancellationToken);
        var stale = await _api.Client.SendAsync(StripeWebhooks.SignedRequest(payload, signedAt: DateTimeOffset.UtcNow.AddMinutes(-10)), TestContext.Current.CancellationToken);
        var tampered = StripeWebhooks.SignedRequest(payload);
        tampered.Content = new StringContent(payload.Replace("price_pro_monthly", "price_business_monthly", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, (await _api.Client.SendAsync(unsigned, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.Client.SendAsync(tampered, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(0, await CountAsync(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == "evt_sig_1")));
    }

    [Fact]
    public async Task Webhook_FromAnotherDeployment_IsAcknowledgedButIgnored()
    {
        var foreign = StripeWebhooks.SubscriptionEvent("evt_foreign_1", "customer.subscription.created", "active", "price_pro_monthly", _t0,
            accountReference: "some-other-customer", subscriptionId: "sub_foreign", customerId: "cus_foreign");

        var response = await Webhook(foreign);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await CountAsync(db => db.Subscriptions.CountAsync(s => s.ProviderSubscriptionId == "sub_foreign")));
        Assert.Equal(1, await CountAsync(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == "evt_foreign_1" && e.Status == Domain.Billing.WebhookEventStatus.Ignored)));
    }

    [Fact]
    public async Task Webhook_ForUnknownPrice_IsStoredAsFailedForRetry()
    {
        var payload = StripeWebhooks.SubscriptionEvent("evt_unknown_price", "customer.subscription.created", "active", "price_not_configured", _t0,
            subscriptionId: "sub_unknown_price");

        var response = await Webhook(payload);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, await CountAsync(db => db.WebhookEvents.CountAsync(e => e.ProviderEventId == "evt_unknown_price" && e.Status == Domain.Billing.WebhookEventStatus.Failed)));
        Assert.Equal(0, await CountAsync(db => db.Subscriptions.CountAsync(s => s.ProviderSubscriptionId == "sub_unknown_price")));
    }

    [Fact]
    public async Task Checkout_RejectsCustomPlansAndUnavailableProviders()
    {
        var owner = await _api.OwnerAsync();

        var enterprise = await Checkout(owner.AccessToken, "enterprise");
        var payPal = await _api.SendAsync(HttpMethod.Post, "/api/billing/checkout", owner.AccessToken, new { planCode = "professional", interval = "Monthly", provider = "PayPal" });
        var unconfiguredInterval = await _api.SendAsync(HttpMethod.Post, "/api/billing/checkout", owner.AccessToken, new { planCode = "business", interval = "Annual", provider = "Stripe" });

        Assert.Equal("billing.plan_not_purchasable", await TestApi.ErrorCodeAsync(enterprise));
        Assert.Equal("billing.provider_unavailable", await TestApi.ErrorCodeAsync(payPal));
        Assert.Contains(await TestApi.ErrorCodeAsync(unconfiguredInterval), new[] { "billing.plan_not_configured", "billing.already_subscribed" });
    }

    [Fact]
    public async Task PayPalWebhook_WhenNotConfigured_IsNotFound()
    {
        using var content = new StringContent("{}");
        var response = await _api.Client.PostAsync("/api/billing/webhooks/paypal", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BillingDetails_RequireBillingPermission()
    {
        var analyst = await _api.MemberAsync(SystemRoles.Analyst);

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/billing/subscription", analyst.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/billing/history", analyst.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/billing/entitlements", analyst.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task PlanEditing_IsDisabledByDefault()
    {
        var owner = await _api.OwnerAsync();

        var response = await _api.SendAsync(HttpMethod.Put, "/api/billing/plans/starter", owner.AccessToken, new
        {
            name = "Starter",
            isActive = true,
            isPublic = true,
            sortOrder = 10,
            isFree = false,
            isCustomPricing = false,
            currency = "USD",
            monthlyPrice = 1m,
            annualPrice = 1m,
            limits = new { maxDatabaseConnections = 999, maxUsers = 999, maxConversationsPerDay = 999, maxApprovedTools = 999, maxToolExecutionsPerMonth = 999 },
            features = Array.Empty<string>(),
            highlights = Array.Empty<string>(),
        });

        Assert.Equal("billing.plan_editing_disabled", await TestApi.ErrorCodeAsync(response));
    }

    private Task<HttpResponseMessage> Checkout(string accessToken, string planCode) =>
        _api.SendAsync(HttpMethod.Post, "/api/billing/checkout", accessToken, new { planCode, interval = "Monthly", provider = "Stripe" });

    private Task<HttpResponseMessage> Webhook(string payload) =>
        _api.Client.SendAsync(StripeWebhooks.SignedRequest(payload), TestContext.Current.CancellationToken);

    private async Task<int> CountAsync(Func<PlatformDbContext, Task<int>> query)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
