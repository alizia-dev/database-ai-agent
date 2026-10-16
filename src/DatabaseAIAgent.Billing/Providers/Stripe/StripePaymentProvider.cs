using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using BillingPortalSessionCreateOptions = Stripe.BillingPortal.SessionCreateOptions;
using BillingPortalSessionService = Stripe.BillingPortal.SessionService;
using PaymentProviderKind = DatabaseAIAgent.Domain.Billing.PaymentProvider;

namespace DatabaseAIAgent.Billing.Providers.Stripe;

/// <summary>
/// Stripe (primary provider). Uses Stripe-hosted Checkout and the customer portal, so card details (Visa, Mastercard,
/// Amex, Apple Pay / Google Pay where available) are entered on Stripe and never reach the platform.
/// </summary>
internal sealed class StripePaymentProvider(IStripeClient client, IOptions<StripeOptions> options, ILogger<StripePaymentProvider> logger) : IPaymentProvider
{
    private const string SignatureHeader = "Stripe-Signature";
    private const string GatewayError = "The payment provider could not complete the request. Please try again in a moment.";

    public PaymentProviderKind Kind => PaymentProviderKind.Stripe;

    public bool IsEnabled => options.Value.IsConfigured;

    public async Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await CallAsync("create checkout session", async () =>
        {
            var metadata = new Dictionary<string, string>
            {
                [StripeEventTranslator.AccountReferenceKey] = request.AccountReference,
                [StripeEventTranslator.PlanCodeKey] = request.PlanCode,
            };

            var customerId = request.ExistingCustomerId;
            if (customerId is null)
            {
                var customer = await new CustomerService(client).CreateAsync(
                    new CustomerCreateOptions { Email = request.CustomerEmail, Metadata = new Dictionary<string, string>(metadata) },
                    IdempotentRequest(),
                    cancellationToken);
                customerId = customer.Id;
            }

            var trial = request.TrialDays is > 0;
            var session = await new SessionService(client).CreateAsync(
                new SessionCreateOptions
                {
                    Mode = "subscription",
                    Customer = customerId,
                    ClientReferenceId = request.AccountReference,
                    LineItems = [new SessionLineItemOptions { Price = request.PriceId, Quantity = 1 }],
                    SuccessUrl = request.SuccessUrl,
                    CancelUrl = request.CancelUrl,
                    Metadata = metadata,
                    AllowPromotionCodes = true,
                    PaymentMethodCollection = trial && !request.TrialRequiresPaymentMethod ? "if_required" : "always",
                    SubscriptionData = new SessionSubscriptionDataOptions
                    {
                        Metadata = new Dictionary<string, string>(metadata),
                        TrialPeriodDays = trial ? request.TrialDays : null,
                    },
                },
                IdempotentRequest(),
                cancellationToken);

            return new CheckoutSession(session.Url, customerId);
        });
    }

    public async Task<string> CreateManagementUrlAsync(ManagementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.ProviderCustomerId))
        {
            throw new PaymentProviderException("There is no Stripe billing profile for this workspace yet.");
        }

        return await CallAsync("create portal session", async () =>
            (await new BillingPortalSessionService(client).CreateAsync(
                new BillingPortalSessionCreateOptions { Customer = request.ProviderCustomerId, ReturnUrl = request.ReturnUrl },
                cancellationToken: cancellationToken)).Url);
    }

    public async Task<ProviderActionResult> ChangePlanAsync(ChangePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await CallAsync("change plan", async () =>
        {
            var service = new SubscriptionService(client);
            var subscription = await service.GetAsync(request.ProviderSubscriptionId, cancellationToken: cancellationToken);
            var item = subscription.Items.Data.FirstOrDefault() ?? throw new PaymentProviderException("The subscription has no items.");

            await service.UpdateAsync(
                request.ProviderSubscriptionId,
                new SubscriptionUpdateOptions
                {
                    Items = [new SubscriptionItemOptions { Id = item.Id, Price = request.NewPriceId }],
                    ProrationBehavior = "create_prorations",
                    CancelAtPeriodEnd = false,
                    Metadata = new Dictionary<string, string> { [StripeEventTranslator.PlanCodeKey] = request.PlanCode },
                },
                IdempotentRequest(),
                cancellationToken);

            // The new state arrives through customer.subscription.updated.
            return new ProviderActionResult(ApprovalUrl: null);
        });
    }

    public Task CancelAsync(string providerSubscriptionId, bool atPeriodEnd, CancellationToken cancellationToken) =>
        CallAsync("cancel subscription", async () =>
        {
            var service = new SubscriptionService(client);
            if (atPeriodEnd)
            {
                await service.UpdateAsync(providerSubscriptionId, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true }, cancellationToken: cancellationToken);
            }
            else
            {
                await service.CancelAsync(providerSubscriptionId, cancellationToken: cancellationToken);
            }

            return true;
        });

    public Task<bool> ResumeAsync(string providerSubscriptionId, CancellationToken cancellationToken) =>
        CallAsync("resume subscription", async () =>
        {
            await new SubscriptionService(client).UpdateAsync(
                providerSubscriptionId,
                new SubscriptionUpdateOptions { CancelAtPeriodEnd = false },
                cancellationToken: cancellationToken);
            return true;
        });

    public Task RefundAsync(string providerPaymentId, decimal? amount, string currency, CancellationToken cancellationToken) =>
        CallAsync("refund payment", async () =>
        {
            await new RefundService(client).CreateAsync(
                new RefundCreateOptions { PaymentIntent = providerPaymentId, Amount = amount is { } a ? Money.ToMinorUnits(a, currency) : null },
                IdempotentRequest(),
                cancellationToken);
            return true;
        });

    public Task<VerifiedWebhook?> VerifyWebhookAsync(WebhookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue(SignatureHeader, out var signature) || string.IsNullOrEmpty(signature))
        {
            return Task.FromResult<VerifiedWebhook?>(null);
        }

        try
        {
            // Verifies the HMAC-SHA256 signature with this endpoint's secret and rejects stale timestamps (replay).
            var stripeEvent = EventUtility.ConstructEvent(
                request.Payload,
                signature,
                options.Value.WebhookSecret,
                options.Value.WebhookToleranceSeconds,
                throwOnApiVersionMismatch: false);
            return Task.FromResult<VerifiedWebhook?>(new VerifiedWebhook(stripeEvent.Id, stripeEvent.Type));
        }
        catch (StripeException)
        {
            return Task.FromResult<VerifiedWebhook?>(null);
        }
    }

    public BillingEvent Interpret(string payload) =>
        StripeEventTranslator.Translate(EventUtility.ParseEvent(payload, throwOnApiVersionMismatch: false));

    private static RequestOptions IdempotentRequest() => new() { IdempotencyKey = Guid.NewGuid().ToString("N") };

    private async Task<T> CallAsync<T>(string operation, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (StripeException ex)
        {
            // Stripe error codes are logged for support; the raw message is not shown to users.
            logger.LogError(ex, "Stripe failed to {Operation}: {StripeCode} ({StatusCode})", operation, ex.StripeError?.Code, ex.HttpStatusCode);
            throw new PaymentProviderException(GatewayError, ex);
        }
    }
}
