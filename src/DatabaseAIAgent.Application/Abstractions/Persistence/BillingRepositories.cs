using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Application.Abstractions.Persistence;

public interface IPlanRepository
{
    Task<IReadOnlyList<Plan>> ListAsync(CancellationToken cancellationToken);

    Task<Plan?> FindByCodeAsync(string code, CancellationToken cancellationToken);

    Task<Plan?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds the plan and interval a provider price/plan ID belongs to.</summary>
    Task<(Plan Plan, BillingInterval Interval)?> FindByProviderPriceIdAsync(PaymentProvider provider, string priceId, CancellationToken cancellationToken);

    void Add(Plan plan);
}

public interface ISubscriptionRepository
{
    /// <summary>The most recent subscription that is not terminal, otherwise the most recent one.</summary>
    Task<Subscription?> GetCurrentAsync(CancellationToken cancellationToken);

    Task<Subscription?> FindByProviderIdAsync(PaymentProvider provider, string providerSubscriptionId, CancellationToken cancellationToken);

    Task<bool> AnyAsync(CancellationToken cancellationToken);

    Task<bool> IsKnownCustomerAsync(PaymentProvider provider, string providerCustomerId, CancellationToken cancellationToken);

    void Add(Subscription subscription);
}

public interface IBillingHistoryRepository
{
    Task<Invoice?> FindInvoiceAsync(PaymentProvider provider, string providerInvoiceId, CancellationToken cancellationToken);

    Task<Payment?> FindPaymentAsync(PaymentProvider provider, string providerPaymentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Invoice>> ListInvoicesAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<Payment>> ListPaymentsAsync(int take, CancellationToken cancellationToken);

    void Add(Invoice invoice);

    void Add(Payment payment);
}

public interface IBillingAccountRepository
{
    /// <summary>The workspace billing account (singleton, seeded at startup).</summary>
    Task<BillingAccount> GetAsync(CancellationToken cancellationToken);

    void Add(BillingAccount account);

    Task<bool> ExistsAsync(CancellationToken cancellationToken);
}

public interface IWebhookEventRepository
{
    Task<WebhookEvent?> FindAsync(PaymentProvider provider, string providerEventId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a newly received event. Returns false when the (provider, event id) pair already exists, including when a
    /// concurrent delivery inserted it first (enforced by a unique index).
    /// </summary>
    Task<bool> TryAddAsync(WebhookEvent webhookEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<WebhookEvent>> ListRetryableAsync(DateTimeOffset receivedBefore, int take, CancellationToken cancellationToken);
}
