using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Billing.Plans;
using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Billing.Subscriptions;

internal sealed class SubscriptionService(
    IEntitlementService entitlements,
    ISubscriptionRepository subscriptions,
    IPlanRepository plans,
    IBillingAccountRepository accounts,
    IEnumerable<IPaymentProvider> providers,
    PlanCatalogService catalog) : ISubscriptionService
{
    public async Task<SubscriptionOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var current = await entitlements.GetCurrentAsync(cancellationToken);
        var subscription = await subscriptions.GetCurrentAsync(cancellationToken);
        var account = await accounts.GetAsync(cancellationToken);
        var plan = await plans.FindByCodeAsync(current.PlanCode, cancellationToken);

        SubscriptionDto? subscriptionDto = subscription is null
            ? null
            : new SubscriptionDto(
                subscription.Provider,
                subscription.Status,
                subscription.Interval,
                subscription.CurrentPeriodEnd,
                subscription.TrialEnd,
                subscription.CancelAtPeriodEnd,
                subscription.CanceledAt,
                CanResume: subscription is { IsTerminal: false, CancelAtPeriodEnd: true, Provider: PaymentProvider.Stripe });

        var managementProvider = subscription?.Provider ?? (account.StripeCustomerId is not null ? PaymentProvider.Stripe : (PaymentProvider?)null);
        return new SubscriptionOverviewDto(
            current,
            plan is null ? null : catalog.ToDto(plan),
            subscriptionDto,
            // Shown as "Payment processing…" until the provider's webhook confirms the subscription.
            CheckoutPending: account.CheckoutStartedAt is not null && (subscription is null || subscription.Status == SubscriptionStatus.Incomplete || subscription.IsTerminal),
            CanManagePaymentMethods: managementProvider is { } kind && providers.Any(p => p.Kind == kind && p.IsEnabled));
    }
}

internal sealed class InvoiceService(IBillingHistoryRepository history) : IInvoiceService
{
    private const int HistoryLength = 24;

    public async Task<BillingHistoryDto> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var invoices = await history.ListInvoicesAsync(HistoryLength, cancellationToken);
        var payments = await history.ListPaymentsAsync(HistoryLength, cancellationToken);
        return new BillingHistoryDto(
            invoices.Select(i => new InvoiceDto(i.Number, i.Status, i.Currency, i.AmountDue, i.AmountPaid, i.IssuedAt, i.PaidAt, i.HostedInvoiceUrl, i.PdfUrl)).ToList(),
            payments.Select(p => new PaymentDto(p.Provider, p.Status, p.Currency, p.Amount, p.AmountRefunded, p.FailureMessage, p.OccurredAt)).ToList());
    }
}
