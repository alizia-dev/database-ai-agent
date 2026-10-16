using System.Globalization;
using System.Text.Json;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Billing.Providers.PayPal;

/// <summary>Maps PayPal webhook payloads (Subscriptions and Payments APIs) to billing events. Pure: no network access.</summary>
internal static class PayPalEventTranslator
{
    public static BillingEvent Translate(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var type = String(root, "event_type") ?? string.Empty;
        var occurredAt = Time(root, "create_time") ?? DateTimeOffset.UtcNow;
        if (!root.TryGetProperty("resource", out var resource))
        {
            return new IgnoredBillingEvent("PayPal event without a resource.", occurredAt);
        }

        return type switch
        {
            _ when type.StartsWith("BILLING.SUBSCRIPTION.", StringComparison.Ordinal) => FromSubscription(resource, occurredAt),
            "PAYMENT.SALE.COMPLETED" => FromSale(resource, PaymentStatus.Succeeded, occurredAt),
            "PAYMENT.SALE.DENIED" => FromSale(resource, PaymentStatus.Failed, occurredAt),
            "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED" => new PaymentRefundedEvent(
                String(resource, "sale_id") ?? string.Empty,
                Amount(resource, "amount", "total"),
                occurredAt),
            _ => new IgnoredBillingEvent($"Unhandled PayPal event type '{type}'.", occurredAt),
        };
    }

    private static SubscriptionChangedEvent FromSubscription(JsonElement subscription, DateTimeOffset occurredAt)
    {
        var status = MapStatus(String(subscription, "status"), FailedPayments(subscription));
        var nextBilling = subscription.TryGetProperty("billing_info", out var billing) ? Time(billing, "next_billing_time") : null;
        var payerId = subscription.TryGetProperty("subscriber", out var subscriber) ? String(subscriber, "payer_id") : null;
        return new SubscriptionChangedEvent(
            String(subscription, "id") ?? string.Empty,
            payerId,
            String(subscription, "custom_id"),
            String(subscription, "plan_id"),
            status,
            Time(subscription, "start_time"),
            nextBilling,
            TrialEnd: null,
            CancelAtPeriodEnd: false,
            status == SubscriptionStatus.Canceled ? Time(subscription, "status_update_time") ?? occurredAt : null,
            Time(subscription, "update_time") ?? occurredAt);
    }

    private static PaymentRecordedEvent FromSale(JsonElement sale, PaymentStatus status, DateTimeOffset occurredAt) => new(
        String(sale, "id") ?? string.Empty,
        ProviderCustomerId: null,
        String(sale, "billing_agreement_id"),
        status,
        Amount(sale, "amount", "total"),
        sale.TryGetProperty("amount", out var amount) ? String(amount, "currency") ?? "USD" : "USD",
        status == PaymentStatus.Failed ? "The payment was declined by PayPal." : null,
        Time(sale, "create_time") ?? occurredAt);

    internal static SubscriptionStatus MapStatus(string? status, int failedPayments) => status switch
    {
        "ACTIVE" when failedPayments > 0 => SubscriptionStatus.PastDue,
        "ACTIVE" => SubscriptionStatus.Active,
        "SUSPENDED" => SubscriptionStatus.PastDue,
        "CANCELLED" or "EXPIRED" => SubscriptionStatus.Canceled,
        _ => SubscriptionStatus.Incomplete, // APPROVAL_PENDING, APPROVED
    };

    private static int FailedPayments(JsonElement subscription) =>
        subscription.TryGetProperty("billing_info", out var billing)
        && billing.TryGetProperty("failed_payments_count", out var count)
        && count.TryGetInt32(out var value) ? value : 0;

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string property) =>
        DateTimeOffset.TryParse(String(element, property), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private static decimal Amount(JsonElement element, string objectProperty, string valueProperty) =>
        element.TryGetProperty(objectProperty, out var amount) ? Money.ParseDecimal(String(amount, valueProperty)) : 0m;
}
