using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Billing.Plans;

public sealed record PlanDto(
    string Code,
    string Name,
    string? Description,
    bool IsFree,
    bool IsCustomPricing,
    string Currency,
    decimal? MonthlyPrice,
    decimal? AnnualPrice,
    int? AnnualSavingsPercent,
    PlanLimitsDto Limits,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<PaymentProvider> CheckoutProviders);

public sealed record PlanCatalogDto(
    IReadOnlyList<PlanDto> Plans,
    int TrialDays,
    bool TrialRequiresPaymentMethod,
    string? SalesContactEmail);

public sealed record UpdatePlanCommand(
    string Name,
    string? Description,
    bool IsActive,
    bool IsPublic,
    int SortOrder,
    bool IsFree,
    bool IsCustomPricing,
    string Currency,
    decimal? MonthlyPrice,
    decimal? AnnualPrice,
    PlanLimitsDto Limits,
    IReadOnlyList<string> Features,
    IReadOnlyList<string> Highlights,
    string? StripeProductId,
    string? StripeMonthlyPriceId,
    string? StripeAnnualPriceId,
    string? PayPalMonthlyPlanId,
    string? PayPalAnnualPlanId);

/// <summary>The plan catalogue: public pricing data and (operator-only) plan editing.</summary>
public sealed class PlanCatalogService(
    IPlanRepository plans,
    IEnumerable<IPaymentProvider> providers,
    IUnitOfWork unitOfWork,
    IOptions<BillingOptions> options,
    ILogger<PlanCatalogService> logger)
{
    public async Task<PlanCatalogDto> GetPublicCatalogAsync(CancellationToken cancellationToken)
    {
        var visible = (await plans.ListAsync(cancellationToken)).Where(p => p.IsActive && p.IsPublic);
        var settings = options.Value;
        return new PlanCatalogDto(visible.Select(ToDto).ToList(), settings.TrialDays, settings.TrialRequiresPaymentMethod, settings.SalesContactEmail);
    }

    public async Task<Result<PlanDto>> UpdateAsync(string code, UpdatePlanCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!options.Value.AllowPlanEditing)
        {
            return Error.Forbidden("billing.plan_editing_disabled", "Plan editing is disabled for this deployment. Plans are managed by your service operator.");
        }

        var plan = await plans.FindByCodeAsync(code, cancellationToken);
        if (plan is null)
        {
            return Error.NotFound("billing.plan_not_found", "That plan was not found.");
        }

        try
        {
            plan.Rename(command.Name, command.Description);
            plan.SetVisibility(command.IsActive, command.IsPublic, command.SortOrder);
            plan.SetPricing(command.IsFree, command.IsCustomPricing, command.Currency, command.MonthlyPrice, command.AnnualPrice);
            var limits = command.Limits;
            plan.SetEntitlements(
                new PlanLimits(limits.MaxDatabaseConnections, limits.MaxUsers, limits.MaxConversationsPerDay, limits.MaxApprovedTools, limits.MaxToolExecutionsPerMonth),
                command.Features,
                command.Highlights);
            plan.SetProviderIds(command.StripeProductId, command.StripeMonthlyPriceId, command.StripeAnnualPriceId, command.PayPalMonthlyPlanId, command.PayPalAnnualPlanId);
        }
        catch (DomainException ex)
        {
            unitOfWork.DiscardChanges();
            return Error.Validation("billing.invalid_plan", ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Plan {PlanCode} was updated", plan.Code);
        return ToDto(plan);
    }

    internal PlanDto ToDto(Plan plan)
    {
        var checkoutProviders = plan.IsPurchasable
            ? providers.Where(p => p.IsEnabled && plan.ProviderPriceId(p.Kind, BillingInterval.Monthly) is not null).Select(p => p.Kind).ToList()
            : [];

        return new PlanDto(
            plan.Code,
            plan.Name,
            plan.Description,
            plan.IsFree,
            plan.IsCustomPricing,
            plan.Currency,
            plan.MonthlyPrice,
            plan.AnnualPrice,
            AnnualSavingsPercent(plan),
            PlanLimitsDto.From(plan.Limits),
            plan.Features,
            plan.Highlights,
            checkoutProviders);
    }

    private static int? AnnualSavingsPercent(Plan plan) =>
        plan is { MonthlyPrice: > 0, AnnualPrice: > 0 }
            ? (int)Math.Round((1 - (plan.AnnualPrice.Value / (plan.MonthlyPrice.Value * 12))) * 100, MidpointRounding.AwayFromZero)
            : null;
}
