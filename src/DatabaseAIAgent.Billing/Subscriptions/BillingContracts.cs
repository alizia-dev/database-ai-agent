using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Billing.Plans;
using DatabaseAIAgent.Domain.Billing;

namespace DatabaseAIAgent.Billing.Subscriptions;

public sealed record StartCheckoutCommand(string PlanCode, BillingInterval Interval, PaymentProvider Provider);

public sealed record ChangePlanCommand(string PlanCode, BillingInterval Interval);

public sealed record CancelSubscriptionCommand(bool AtPeriodEnd);

public sealed record RedirectDto(string Url);

/// <summary>Result of a plan change: either applied by the provider (confirmed by webhook) or awaiting approval.</summary>
public sealed record PlanChangeDto(string Status, string? ApprovalUrl);

public sealed record SubscriptionDto(
    PaymentProvider Provider,
    SubscriptionStatus Status,
    BillingInterval Interval,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset? TrialEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? CanceledAt,
    bool CanResume);

public sealed record SubscriptionOverviewDto(
    Entitlements Entitlements,
    PlanDto? Plan,
    SubscriptionDto? Subscription,
    bool CheckoutPending,
    bool CanManagePaymentMethods);

public sealed record InvoiceDto(
    string? Number,
    InvoiceStatus Status,
    string Currency,
    decimal AmountDue,
    decimal AmountPaid,
    DateTimeOffset IssuedAt,
    DateTimeOffset? PaidAt,
    string? HostedInvoiceUrl,
    string? PdfUrl);

public sealed record PaymentDto(PaymentProvider Provider, PaymentStatus Status, string Currency, decimal Amount, decimal AmountRefunded, string? FailureMessage, DateTimeOffset OccurredAt);

public sealed record BillingHistoryDto(IReadOnlyList<InvoiceDto> Invoices, IReadOnlyList<PaymentDto> Payments);

/// <summary>Starts and changes subscriptions with the payment provider. Access changes only via verified webhooks.</summary>
public interface IBillingService
{
    Task<Result<RedirectDto>> StartCheckoutAsync(StartCheckoutCommand command, CancellationToken cancellationToken);

    Task<Result<RedirectDto>> CreatePortalSessionAsync(CancellationToken cancellationToken);

    Task<Result<PlanChangeDto>> ChangePlanAsync(ChangePlanCommand command, CancellationToken cancellationToken);

    Task<Result> CancelAsync(CancelSubscriptionCommand command, CancellationToken cancellationToken);

    Task<Result> ResumeAsync(CancellationToken cancellationToken);
}

public interface ISubscriptionService
{
    Task<SubscriptionOverviewDto> GetOverviewAsync(CancellationToken cancellationToken);
}

public interface IInvoiceService
{
    Task<BillingHistoryDto> GetHistoryAsync(CancellationToken cancellationToken);
}
