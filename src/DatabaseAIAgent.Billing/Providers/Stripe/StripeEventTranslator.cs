using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Domain.Billing;
using Stripe;
using Stripe.Checkout;
using StripeSubscription = Stripe.Subscription;
using StripeInvoice = Stripe.Invoice;

namespace DatabaseAIAgent.Billing.Providers.Stripe;

/// <summary>Maps Stripe event objects to provider-neutral billing events. Pure: no network access.</summary>
internal static class StripeEventTranslator
{
    public const string AccountReferenceKey = "account_reference";
    public const string PlanCodeKey = "plan_code";

    public static BillingEvent Translate(Event stripeEvent)
    {
        var occurredAt = Utc(stripeEvent.Created);
        return stripeEvent.Data.Object switch
        {
            StripeSubscription subscription when stripeEvent.Type.StartsWith("customer.subscription.", StringComparison.Ordinal) =>
                FromSubscription(subscription, occurredAt),
            Session { Mode: "subscription" } session when stripeEvent.Type == EventTypes.CheckoutSessionCompleted =>
                new CheckoutCompletedEvent(session.ClientReferenceId ?? Metadata(session.Metadata, AccountReferenceKey), session.CustomerId, session.SubscriptionId, occurredAt),
            StripeInvoice invoice when stripeEvent.Type.StartsWith("invoice.", StringComparison.Ordinal) =>
                FromInvoice(invoice, occurredAt),
            PaymentIntent intent when stripeEvent.Type is EventTypes.PaymentIntentSucceeded or EventTypes.PaymentIntentPaymentFailed =>
                FromPaymentIntent(intent, stripeEvent.Type == EventTypes.PaymentIntentSucceeded, occurredAt),
            Charge charge when stripeEvent.Type == EventTypes.ChargeRefunded && charge.PaymentIntentId is not null =>
                new PaymentRefundedEvent(charge.PaymentIntentId, Money.FromMinorUnits(charge.AmountRefunded, charge.Currency), occurredAt),
            _ => new IgnoredBillingEvent($"Unhandled Stripe event type '{stripeEvent.Type}'.", occurredAt),
        };
    }

    private static SubscriptionChangedEvent FromSubscription(StripeSubscription subscription, DateTimeOffset occurredAt)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();
        return new SubscriptionChangedEvent(
            subscription.Id,
            subscription.CustomerId,
            Metadata(subscription.Metadata, AccountReferenceKey),
            item?.Price?.Id,
            MapStatus(subscription.Status),
            item is null ? null : Utc(item.CurrentPeriodStart),
            item is null ? null : Utc(item.CurrentPeriodEnd),
            subscription.TrialEnd is { } trialEnd ? Utc(trialEnd) : null,
            // The customer portal schedules cancellation with cancel_at; treat both as "ends at period end".
            subscription.CancelAtPeriodEnd || subscription.CancelAt.HasValue,
            subscription.CanceledAt is { } canceledAt ? Utc(canceledAt) : null,
            occurredAt);
    }

    private static InvoiceChangedEvent FromInvoice(StripeInvoice invoice, DateTimeOffset occurredAt)
    {
        var currency = invoice.Currency ?? "usd";
        return new InvoiceChangedEvent(
            invoice.Id,
            invoice.CustomerId,
            invoice.Parent?.SubscriptionDetails?.SubscriptionId,
            new InvoiceSnapshot(
                invoice.Number,
                MapInvoiceStatus(invoice.Status),
                currency,
                Money.FromMinorUnits(invoice.AmountDue, currency),
                Money.FromMinorUnits(invoice.AmountPaid, currency),
                Utc(invoice.PeriodStart),
                Utc(invoice.PeriodEnd),
                Utc(invoice.Created),
                invoice.StatusTransitions?.PaidAt is { } paidAt ? Utc(paidAt) : null,
                invoice.HostedInvoiceUrl,
                invoice.InvoicePdf),
            occurredAt);
    }

    private static PaymentRecordedEvent FromPaymentIntent(PaymentIntent intent, bool succeeded, DateTimeOffset occurredAt)
    {
        var currency = intent.Currency ?? "usd";
        var amount = succeeded && intent.AmountReceived > 0 ? intent.AmountReceived : intent.Amount;
        return new PaymentRecordedEvent(
            intent.Id,
            intent.CustomerId,
            ProviderSubscriptionId: null,
            succeeded ? PaymentStatus.Succeeded : PaymentStatus.Failed,
            Money.FromMinorUnits(amount, currency),
            currency,
            // Stripe's decline messages are written for end customers (e.g. "Your card was declined.").
            succeeded ? null : intent.LastPaymentError?.Message,
            occurredAt);
    }

    internal static SubscriptionStatus MapStatus(string? status) => status switch
    {
        "trialing" => SubscriptionStatus.Trialing,
        "active" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "unpaid" => SubscriptionStatus.Unpaid,
        "canceled" => SubscriptionStatus.Canceled,
        "paused" => SubscriptionStatus.Paused,
        "incomplete_expired" => SubscriptionStatus.IncompleteExpired,
        _ => SubscriptionStatus.Incomplete,
    };

    private static InvoiceStatus MapInvoiceStatus(string? status) => status switch
    {
        "paid" => InvoiceStatus.Paid,
        "open" => InvoiceStatus.Open,
        "void" => InvoiceStatus.Void,
        "uncollectible" => InvoiceStatus.Uncollectible,
        _ => InvoiceStatus.Draft,
    };

    private static string? Metadata(Dictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
