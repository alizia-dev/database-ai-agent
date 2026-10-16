using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Billing.Webhooks;

public enum ApplyOutcome
{
    Applied = 0,
    Ignored = 1,
}

/// <summary>Raised when an event references configuration this deployment lacks (e.g. an unknown price ID). Retried later.</summary>
public sealed class BillingConfigurationException(string message) : Exception(message)
{
    public BillingConfigurationException()
        : this("Billing configuration is incomplete.")
    {
    }
}

/// <summary>
/// Applies verified, provider-neutral billing events to the platform database. Only events that belong to this
/// deployment (matching account reference, or a customer/subscription this deployment already knows) are applied.
/// Changes are tracked; the caller saves them together with the webhook's processed state.
/// </summary>
public sealed class BillingEventApplier(
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IBillingHistoryRepository history,
    IBillingAccountRepository accounts,
    IOptions<BillingOptions> options,
    ILogger<BillingEventApplier> logger)
{
    public Task<ApplyOutcome> ApplyAsync(PaymentProvider provider, BillingEvent billingEvent, CancellationToken cancellationToken) => billingEvent switch
    {
        SubscriptionChangedEvent e => ApplySubscriptionAsync(provider, e, cancellationToken),
        CheckoutCompletedEvent e => ApplyCheckoutAsync(provider, e, cancellationToken),
        InvoiceChangedEvent e => ApplyInvoiceAsync(provider, e, cancellationToken),
        PaymentRecordedEvent e => ApplyPaymentAsync(provider, e, cancellationToken),
        PaymentRefundedEvent e => ApplyRefundAsync(provider, e, cancellationToken),
        _ => Task.FromResult(ApplyOutcome.Ignored),
    };

    private async Task<ApplyOutcome> ApplySubscriptionAsync(PaymentProvider provider, SubscriptionChangedEvent e, CancellationToken ct)
    {
        if (!await BelongsToThisDeploymentAsync(provider, e.AccountReference, e.ProviderCustomerId, e.ProviderSubscriptionId, ct))
        {
            return Ignored("subscription belongs to another account", e.ProviderSubscriptionId);
        }

        if (string.IsNullOrEmpty(e.PriceId))
        {
            throw new BillingConfigurationException("The subscription has no price.");
        }

        var match = await plans.FindByProviderPriceIdAsync(provider, e.PriceId, ct)
            ?? throw new BillingConfigurationException($"No plan is configured for {provider} price '{e.PriceId}'.");

        var subscription = await subscriptions.FindByProviderIdAsync(provider, e.ProviderSubscriptionId, ct);
        if (subscription is null)
        {
            subscription = new Subscription(match.Plan.Id, provider, e.ProviderSubscriptionId, e.ProviderCustomerId, match.Interval);
            subscriptions.Add(subscription);
        }

        var applied = subscription.Apply(new SubscriptionSnapshot(
            match.Plan.Id,
            match.Interval,
            e.Status,
            e.ProviderCustomerId,
            e.CurrentPeriodStart,
            e.CurrentPeriodEnd,
            e.TrialEnd,
            e.CancelAtPeriodEnd,
            e.CanceledAt,
            e.OccurredAt));

        if (!applied)
        {
            return Ignored("event is older than the subscription state", e.ProviderSubscriptionId);
        }

        var account = await accounts.GetAsync(ct);
        if (provider == PaymentProvider.Stripe && account.StripeCustomerId is null && e.ProviderCustomerId is not null)
        {
            account.LinkStripeCustomer(e.ProviderCustomerId);
        }

        if (e.Status is not SubscriptionStatus.Incomplete)
        {
            account.ClearPendingCheckout();
        }

        logger.LogInformation("Subscription {SubscriptionId} ({Provider}) is now {Status} on plan {PlanCode}", subscription.Id, provider, e.Status, match.Plan.Code);
        return ApplyOutcome.Applied;
    }

    private async Task<ApplyOutcome> ApplyCheckoutAsync(PaymentProvider provider, CheckoutCompletedEvent e, CancellationToken ct)
    {
        if (!await BelongsToThisDeploymentAsync(provider, e.AccountReference, e.ProviderCustomerId, e.ProviderSubscriptionId, ct))
        {
            return Ignored("checkout belongs to another account", e.ProviderSubscriptionId ?? "checkout");
        }

        var account = await accounts.GetAsync(ct);
        if (provider == PaymentProvider.Stripe && account.StripeCustomerId is null && e.ProviderCustomerId is not null)
        {
            account.LinkStripeCustomer(e.ProviderCustomerId);
        }

        // Activation is driven by the subscription events; checkout completion alone grants nothing.
        return ApplyOutcome.Applied;
    }

    private async Task<ApplyOutcome> ApplyInvoiceAsync(PaymentProvider provider, InvoiceChangedEvent e, CancellationToken ct)
    {
        if (!await BelongsToThisDeploymentAsync(provider, null, e.ProviderCustomerId, e.ProviderSubscriptionId, ct))
        {
            return Ignored("invoice belongs to another account", e.ProviderInvoiceId);
        }

        var invoice = await history.FindInvoiceAsync(provider, e.ProviderInvoiceId, ct);
        if (invoice is null)
        {
            invoice = new Invoice(provider, e.ProviderInvoiceId);
            history.Add(invoice);
        }

        var subscription = e.ProviderSubscriptionId is null ? null : await subscriptions.FindByProviderIdAsync(provider, e.ProviderSubscriptionId, ct);
        invoice.Update(e.Invoice, subscription?.Id);
        return ApplyOutcome.Applied;
    }

    private async Task<ApplyOutcome> ApplyPaymentAsync(PaymentProvider provider, PaymentRecordedEvent e, CancellationToken ct)
    {
        if (!await BelongsToThisDeploymentAsync(provider, null, e.ProviderCustomerId, e.ProviderSubscriptionId, ct))
        {
            return Ignored("payment belongs to another account", e.ProviderPaymentId);
        }

        var payment = await history.FindPaymentAsync(provider, e.ProviderPaymentId, ct);
        if (payment is null)
        {
            payment = new Payment(provider, e.ProviderPaymentId);
            history.Add(payment);
        }

        payment.Record(e.Status, e.Currency, e.Amount, e.FailureMessage, e.OccurredAt, invoiceId: null);
        if (e.Status == PaymentStatus.Failed)
        {
            logger.LogWarning("A {Provider} payment failed ({PaymentId})", provider, e.ProviderPaymentId);
        }

        return ApplyOutcome.Applied;
    }

    private async Task<ApplyOutcome> ApplyRefundAsync(PaymentProvider provider, PaymentRefundedEvent e, CancellationToken ct)
    {
        // Only payments this deployment recorded can be refunded here, which also scopes the event to this account.
        var payment = await history.FindPaymentAsync(provider, e.ProviderPaymentId, ct);
        if (payment is null)
        {
            return Ignored("refund for an unknown payment", e.ProviderPaymentId);
        }

        payment.RecordRefund(e.AmountRefunded);
        return ApplyOutcome.Applied;
    }

    private async Task<bool> BelongsToThisDeploymentAsync(PaymentProvider provider, string? accountReference, string? customerId, string? subscriptionId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(accountReference))
        {
            return string.Equals(accountReference, options.Value.AccountReference, StringComparison.Ordinal);
        }

        if (customerId is not null)
        {
            var account = await accounts.GetAsync(ct);
            if (provider == PaymentProvider.Stripe && string.Equals(account.StripeCustomerId, customerId, StringComparison.Ordinal))
            {
                return true;
            }

            if (await subscriptions.IsKnownCustomerAsync(provider, customerId, ct))
            {
                return true;
            }
        }

        return subscriptionId is not null && await subscriptions.FindByProviderIdAsync(provider, subscriptionId, ct) is not null;
    }

    private ApplyOutcome Ignored(string reason, string reference)
    {
        logger.LogInformation("Ignored billing event for {Reference}: {Reason}", reference, reason);
        return ApplyOutcome.Ignored;
    }
}
