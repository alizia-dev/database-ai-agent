using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence.Repositories;

internal sealed class PlanRepository(PlatformDbContext db) : IPlanRepository
{
    public async Task<IReadOnlyList<Plan>> ListAsync(CancellationToken cancellationToken) =>
        await db.Plans.OrderBy(p => p.SortOrder).ThenBy(p => p.Code).ToListAsync(cancellationToken);

    public Task<Plan?> FindByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToLowerInvariant();
        return db.Plans.SingleOrDefaultAsync(p => p.Code == normalized, cancellationToken);
    }

    public Task<Plan?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Plans.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<(Plan Plan, BillingInterval Interval)?> FindByProviderPriceIdAsync(PaymentProvider provider, string priceId, CancellationToken cancellationToken)
    {
        var plan = provider switch
        {
            PaymentProvider.Stripe => await db.Plans.FirstOrDefaultAsync(p => p.StripeMonthlyPriceId == priceId || p.StripeAnnualPriceId == priceId, cancellationToken),
            PaymentProvider.PayPal => await db.Plans.FirstOrDefaultAsync(p => p.PayPalMonthlyPlanId == priceId || p.PayPalAnnualPlanId == priceId, cancellationToken),
            _ => null,
        };

        if (plan is null)
        {
            return null;
        }

        var annual = plan.ProviderPriceId(provider, BillingInterval.Annual) == priceId;
        return (plan, annual ? BillingInterval.Annual : BillingInterval.Monthly);
    }

    public void Add(Plan plan) => db.Plans.Add(plan);
}

internal sealed class SubscriptionRepository(PlatformDbContext db) : ISubscriptionRepository
{
    private static readonly SubscriptionStatus[] TerminalStatuses = [SubscriptionStatus.Canceled, SubscriptionStatus.IncompleteExpired];

    public async Task<Subscription?> GetCurrentAsync(CancellationToken cancellationToken) =>
        await db.Subscriptions
            .OrderBy(s => TerminalStatuses.Contains(s.Status) ? 1 : 0)
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<Subscription?> FindByProviderIdAsync(PaymentProvider provider, string providerSubscriptionId, CancellationToken cancellationToken) =>
        db.Subscriptions.SingleOrDefaultAsync(s => s.Provider == provider && s.ProviderSubscriptionId == providerSubscriptionId, cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken) => db.Subscriptions.AnyAsync(cancellationToken);

    public Task<bool> IsKnownCustomerAsync(PaymentProvider provider, string providerCustomerId, CancellationToken cancellationToken) =>
        db.Subscriptions.AnyAsync(s => s.Provider == provider && s.ProviderCustomerId == providerCustomerId, cancellationToken);

    public void Add(Subscription subscription) => db.Subscriptions.Add(subscription);
}

internal sealed class BillingHistoryRepository(PlatformDbContext db) : IBillingHistoryRepository
{
    public Task<Invoice?> FindInvoiceAsync(PaymentProvider provider, string providerInvoiceId, CancellationToken cancellationToken) =>
        db.Invoices.SingleOrDefaultAsync(i => i.Provider == provider && i.ProviderInvoiceId == providerInvoiceId, cancellationToken);

    public Task<Payment?> FindPaymentAsync(PaymentProvider provider, string providerPaymentId, CancellationToken cancellationToken) =>
        db.Payments.SingleOrDefaultAsync(p => p.Provider == provider && p.ProviderPaymentId == providerPaymentId, cancellationToken);

    public async Task<IReadOnlyList<Invoice>> ListInvoicesAsync(int take, CancellationToken cancellationToken) =>
        await db.Invoices.AsNoTracking().OrderByDescending(i => i.IssuedAt).Take(take).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Payment>> ListPaymentsAsync(int take, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking().OrderByDescending(p => p.OccurredAt).Take(take).ToListAsync(cancellationToken);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public void Add(Payment payment) => db.Payments.Add(payment);
}

internal sealed class BillingAccountRepository(PlatformDbContext db) : IBillingAccountRepository
{
    public async Task<BillingAccount> GetAsync(CancellationToken cancellationToken) =>
        await db.BillingAccounts.SingleOrDefaultAsync(a => a.Id == BillingAccount.SingletonId, cancellationToken)
        ?? throw new InvalidOperationException("The billing account has not been seeded.");

    public void Add(BillingAccount account) => db.BillingAccounts.Add(account);

    public Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
        db.BillingAccounts.AnyAsync(a => a.Id == BillingAccount.SingletonId, cancellationToken);
}

internal sealed class WebhookEventRepository(PlatformDbContext db) : IWebhookEventRepository
{
    public Task<WebhookEvent?> FindAsync(PaymentProvider provider, string providerEventId, CancellationToken cancellationToken) =>
        db.WebhookEvents.SingleOrDefaultAsync(e => e.Provider == provider && e.ProviderEventId == providerEventId, cancellationToken);

    public async Task<bool> TryAddAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        if (await db.WebhookEvents.AnyAsync(e => e.Provider == webhookEvent.Provider && e.ProviderEventId == webhookEvent.ProviderEventId, cancellationToken))
        {
            return false;
        }

        db.WebhookEvents.Add(webhookEvent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event won the race to the unique index.
            db.Entry(webhookEvent).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<WebhookEvent>> ListRetryableAsync(DateTimeOffset receivedBefore, int take, CancellationToken cancellationToken)
    {
        var candidates = await db.WebhookEvents
            .Where(e => (e.Status == WebhookEventStatus.Received || e.Status == WebhookEventStatus.Failed) && e.Attempts < WebhookEvent.MaxAttempts)
            .OrderBy(e => e.ReceivedAt)
            .Take(take * 2)
            .ToListAsync(cancellationToken);

        // Timestamp comparison in memory keeps the query portable across providers (DateTimeOffset on SQLite).
        return candidates.Where(e => e.ReceivedAt < receivedBefore).Take(take).ToList();
    }
}
