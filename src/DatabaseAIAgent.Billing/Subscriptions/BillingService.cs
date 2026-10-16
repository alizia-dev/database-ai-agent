using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Billing.Subscriptions;

internal sealed class BillingService(
    IEnumerable<IPaymentProvider> providers,
    IPlanRepository plans,
    ISubscriptionRepository subscriptions,
    IBillingAccountRepository accounts,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<BillingOptions> options,
    IOptions<AppUrlOptions> appUrl,
    ILogger<BillingService> logger) : IBillingService
{
    private const string BillingPath = "/app/billing";

    private static readonly Error NoSubscription = Error.NotFound("billing.no_subscription", "This workspace has no active subscription.");
    private static readonly Error ProviderUnavailable = Error.Validation("billing.provider_unavailable", "This payment method is not available. Choose another one or contact support.");

    public async Task<Result<RedirectDto>> StartCheckoutAsync(StartCheckoutCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var plan = await plans.FindByCodeAsync(command.PlanCode, cancellationToken);
        if (plan is not { IsPurchasable: true })
        {
            return Error.Validation("billing.plan_not_purchasable", "That plan cannot be purchased online. Contact sales for custom plans.");
        }

        var provider = Provider(command.Provider);
        if (provider is null)
        {
            return ProviderUnavailable;
        }

        var priceId = plan.ProviderPriceId(command.Provider, command.Interval);
        if (priceId is null)
        {
            return Error.Validation("billing.plan_not_configured", "This plan is not yet available with the selected payment method and billing period.");
        }

        var current = await subscriptions.GetCurrentAsync(cancellationToken);
        if (current is { IsTerminal: false, Status: not SubscriptionStatus.Incomplete })
        {
            return Error.Conflict("billing.already_subscribed", "This workspace already has a subscription. Change the plan from the billing page instead.");
        }

        var email = currentUser.Email ?? throw new InvalidOperationException("Authenticated user has no email claim.");
        var account = await accounts.GetAsync(cancellationToken);
        var isFirstSubscription = !await subscriptions.AnyAsync(cancellationToken);
        var settings = options.Value;

        try
        {
            var session = await provider.CreateCheckoutAsync(new CheckoutRequest(
                plan.Code,
                priceId,
                command.Interval,
                settings.AccountReference,
                command.Provider == PaymentProvider.Stripe ? account.StripeCustomerId : null,
                email,
                isFirstSubscription && settings.TrialDays > 0 ? settings.TrialDays : null,
                settings.TrialRequiresPaymentMethod,
                appUrl.Value.Link($"{BillingPath}?checkout=success"),
                appUrl.Value.Link($"{BillingPath}?checkout=canceled")), cancellationToken);

            if (command.Provider == PaymentProvider.Stripe && account.StripeCustomerId is null && session.ProviderCustomerId is not null)
            {
                account.LinkStripeCustomer(session.ProviderCustomerId);
            }

            account.SetBillingEmail(email);
            account.MarkCheckoutStarted(clock.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation("User {UserId} started {Provider} checkout for plan {PlanCode} ({Interval})", currentUser.UserId, command.Provider, plan.Code, command.Interval);
            return new RedirectDto(session.RedirectUrl);
        }
        catch (PaymentProviderException ex)
        {
            return Error.Unavailable("billing.provider_error", ex.Message);
        }
    }

    public async Task<Result<RedirectDto>> CreatePortalSessionAsync(CancellationToken cancellationToken)
    {
        var current = await subscriptions.GetCurrentAsync(cancellationToken);
        var account = await accounts.GetAsync(cancellationToken);
        var kind = current?.Provider ?? (account.StripeCustomerId is not null ? PaymentProvider.Stripe : (PaymentProvider?)null);
        var provider = kind is { } k ? Provider(k) : null;
        if (provider is null)
        {
            return NoSubscription;
        }

        try
        {
            var customerId = provider.Kind == PaymentProvider.Stripe ? account.StripeCustomerId ?? current?.ProviderCustomerId : current?.ProviderCustomerId;
            var url = await provider.CreateManagementUrlAsync(new ManagementRequest(customerId, current?.ProviderSubscriptionId, appUrl.Value.Link(BillingPath)), cancellationToken);
            return new RedirectDto(url);
        }
        catch (PaymentProviderException ex)
        {
            return Error.Unavailable("billing.provider_error", ex.Message);
        }
    }

    public async Task<Result<PlanChangeDto>> ChangePlanAsync(ChangePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await subscriptions.GetCurrentAsync(cancellationToken);
        if (current is null || current.IsTerminal)
        {
            return NoSubscription;
        }

        var plan = await plans.FindByCodeAsync(command.PlanCode, cancellationToken);
        if (plan is not { IsPurchasable: true })
        {
            return Error.Validation("billing.plan_not_purchasable", "That plan cannot be purchased online. Contact sales for custom plans.");
        }

        if (plan.Id == current.PlanId && command.Interval == current.Interval)
        {
            return Error.Validation("billing.same_plan", "You are already on this plan.");
        }

        var provider = Provider(current.Provider);
        var priceId = plan.ProviderPriceId(current.Provider, command.Interval);
        if (provider is null || priceId is null)
        {
            return Error.Validation("billing.plan_not_configured", "This plan is not available with your current payment method.");
        }

        try
        {
            var result = await provider.ChangePlanAsync(new ChangePlanRequest(
                current.ProviderSubscriptionId,
                plan.Code,
                priceId,
                appUrl.Value.Link($"{BillingPath}?change=success"),
                appUrl.Value.Link($"{BillingPath}?change=canceled")), cancellationToken);

            logger.LogInformation("User {UserId} requested plan change to {PlanCode} ({Interval})", currentUser.UserId, plan.Code, command.Interval);
            return result.ApprovalUrl is null
                ? new PlanChangeDto("processing", null)
                : new PlanChangeDto("approval_required", result.ApprovalUrl);
        }
        catch (PaymentProviderException ex)
        {
            return Error.Unavailable("billing.provider_error", ex.Message);
        }
    }

    public async Task<Result> CancelAsync(CancelSubscriptionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = await subscriptions.GetCurrentAsync(cancellationToken);
        var provider = current is { IsTerminal: false } ? Provider(current.Provider) : null;
        if (current is null || provider is null)
        {
            return NoSubscription;
        }

        try
        {
            await provider.CancelAsync(current.ProviderSubscriptionId, command.AtPeriodEnd, cancellationToken);
            logger.LogInformation("User {UserId} canceled the subscription (at period end: {AtPeriodEnd})", currentUser.UserId, command.AtPeriodEnd);
            return Result.Success();
        }
        catch (PaymentProviderException ex)
        {
            return Error.Unavailable("billing.provider_error", ex.Message);
        }
    }

    public async Task<Result> ResumeAsync(CancellationToken cancellationToken)
    {
        var current = await subscriptions.GetCurrentAsync(cancellationToken);
        var provider = current is { IsTerminal: false, CancelAtPeriodEnd: true } ? Provider(current.Provider) : null;
        if (current is null || provider is null)
        {
            return Error.Validation("billing.nothing_to_resume", "There is no scheduled cancellation to undo.");
        }

        try
        {
            return await provider.ResumeAsync(current.ProviderSubscriptionId, cancellationToken)
                ? Result.Success()
                : Error.Validation("billing.resume_unsupported", "This payment method does not support undoing a cancellation. Start a new subscription instead.");
        }
        catch (PaymentProviderException ex)
        {
            return Error.Unavailable("billing.provider_error", ex.Message);
        }
    }

    private IPaymentProvider? Provider(PaymentProvider kind) => providers.FirstOrDefault(p => p.Kind == kind && p.IsEnabled);
}
