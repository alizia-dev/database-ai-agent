using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Billing.Plans;

/// <summary>
/// Seeds the plan catalogue from configuration (<c>Billing:Plans</c>) and the billing-account singleton.
/// Existing plans are never overwritten, except that empty provider IDs are filled from configuration, so a deployment
/// can supply its Stripe/PayPal price IDs through environment variables after first start.
/// </summary>
internal sealed class BillingDataSeeder(
    IPlanRepository plans,
    IBillingAccountRepository accounts,
    IUnitOfWork unitOfWork,
    IOptions<BillingOptions> options,
    ILogger<BillingDataSeeder> logger) : IDataSeeder
{
    public int Order => 100;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!await accounts.ExistsAsync(cancellationToken))
        {
            accounts.Add(BillingAccount.Create());
        }

        var existing = (await plans.ListAsync(cancellationToken)).ToDictionary(p => p.Code, StringComparer.Ordinal);
        foreach (var seed in options.Value.Plans)
        {
            var code = seed.Code.Trim().ToLowerInvariant();
            if (existing.TryGetValue(code, out var plan))
            {
                FillMissingProviderIds(plan, seed);
                continue;
            }

            plans.Add(Create(seed));
            logger.LogInformation("Seeded billing plan {PlanCode}", code);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    internal static Plan Create(PlanSeed seed)
    {
        var plan = new Plan(seed.Code, seed.Name, seed.SortOrder);
        plan.Rename(seed.Name, seed.Description);
        plan.SetVisibility(isActive: true, seed.IsPublic, seed.SortOrder);
        plan.SetPricing(seed.IsFree, seed.IsCustomPricing, seed.Currency, seed.MonthlyPrice, seed.AnnualPrice);
        plan.SetEntitlements(
            new PlanLimits(seed.MaxDatabaseConnections, seed.MaxUsers, seed.MaxConversationsPerDay, seed.MaxApprovedTools, seed.MaxToolExecutionsPerMonth),
            seed.Features,
            seed.Highlights);
        plan.SetProviderIds(seed.StripeProductId, seed.StripeMonthlyPriceId, seed.StripeAnnualPriceId, seed.PayPalMonthlyPlanId, seed.PayPalAnnualPlanId);
        return plan;
    }

    private static void FillMissingProviderIds(Plan plan, PlanSeed seed) =>
        plan.SetProviderIds(
            plan.StripeProductId ?? seed.StripeProductId,
            plan.StripeMonthlyPriceId ?? seed.StripeMonthlyPriceId,
            plan.StripeAnnualPriceId ?? seed.StripeAnnualPriceId,
            plan.PayPalMonthlyPlanId ?? seed.PayPalMonthlyPlanId,
            plan.PayPalAnnualPlanId ?? seed.PayPalAnnualPlanId);
}
