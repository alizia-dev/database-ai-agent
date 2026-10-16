using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Billing;

/// <summary>
/// A subscription plan. Plans are data (seeded from configuration, editable by an operator), never hard-coded.
/// Displayed prices are informational; the amount actually charged is defined by the provider price/plan IDs.
/// </summary>
public sealed class Plan : AuditableEntity
{
    public const int CodeMaxLength = 40;
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 500;
    public const int ProviderIdMaxLength = 100;

    private Plan()
    {
    }

    public Plan(string code, string name, int sortOrder)
    {
        Code = ValidCode(code);
        Rename(name, description: null);
        SortOrder = sortOrder;
        IsActive = true;
        IsPublic = true;
    }

    /// <summary>Stable identifier used by APIs and provider metadata (e.g. "professional").</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int SortOrder { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Shown on the public pricing page.</summary>
    public bool IsPublic { get; private set; }

    /// <summary>The free/demo plan every workspace falls back to without a paid subscription.</summary>
    public bool IsFree { get; private set; }

    /// <summary>Negotiated pricing (Enterprise): no self-service checkout.</summary>
    public bool IsCustomPricing { get; private set; }

    public string Currency { get; private set; } = "USD";

    public decimal? MonthlyPrice { get; private set; }

    public decimal? AnnualPrice { get; private set; }

    public PlanLimits Limits { get; private set; } = PlanLimits.None;

    /// <summary>Feature flags granted by the plan (see <see cref="PlanFeatures"/>).</summary>
    public List<string> Features { get; private set; } = [];

    /// <summary>Marketing bullet points for the pricing page.</summary>
    public List<string> Highlights { get; private set; } = [];

    public string? StripeProductId { get; private set; }

    public string? StripeMonthlyPriceId { get; private set; }

    public string? StripeAnnualPriceId { get; private set; }

    public string? PayPalMonthlyPlanId { get; private set; }

    public string? PayPalAnnualPlanId { get; private set; }

    public bool HasFeature(string feature) => Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>Self-service checkout is possible for paid, non-custom plans.</summary>
    public bool IsPurchasable => IsActive && !IsFree && !IsCustomPricing;

    public void Rename(string name, string? description)
    {
        Name = Text.Required(name, nameof(Name), NameMaxLength);
        Description = Text.Optional(description, nameof(Description), DescriptionMaxLength);
    }

    public void SetVisibility(bool isActive, bool isPublic, int sortOrder)
    {
        IsActive = isActive;
        IsPublic = isPublic;
        SortOrder = sortOrder;
    }

    public void SetPricing(bool isFree, bool isCustomPricing, string currency, decimal? monthlyPrice, decimal? annualPrice)
    {
        if (isFree && isCustomPricing)
        {
            throw new DomainException("A plan cannot be both free and custom-priced.");
        }

        if (monthlyPrice < 0 || annualPrice < 0)
        {
            throw new DomainException("Prices cannot be negative.");
        }

        var code = Text.Required(currency, nameof(Currency), 3).ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
        {
            throw new DomainException("Currency must be a 3-letter ISO 4217 code.");
        }

        IsFree = isFree;
        IsCustomPricing = isCustomPricing;
        Currency = code;
        MonthlyPrice = isFree ? 0 : monthlyPrice;
        AnnualPrice = isFree ? 0 : annualPrice;
    }

    public void SetEntitlements(PlanLimits limits, IEnumerable<string> features, IEnumerable<string> highlights)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        var featureList = features.Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var unknown = featureList.Where(f => !PlanFeatures.IsKnown(f)).ToArray();
        if (unknown.Length > 0)
        {
            throw new DomainException($"Unknown plan feature(s): {string.Join(", ", unknown)}.");
        }

        Limits = limits;
        Features = featureList;
        Highlights = highlights.Select(h => h.Trim()).Where(h => h.Length > 0).Take(20).ToList();
    }

    public void SetProviderIds(string? stripeProductId, string? stripeMonthlyPriceId, string? stripeAnnualPriceId, string? payPalMonthlyPlanId, string? payPalAnnualPlanId)
    {
        StripeProductId = Text.Optional(stripeProductId, nameof(StripeProductId), ProviderIdMaxLength);
        StripeMonthlyPriceId = Text.Optional(stripeMonthlyPriceId, nameof(StripeMonthlyPriceId), ProviderIdMaxLength);
        StripeAnnualPriceId = Text.Optional(stripeAnnualPriceId, nameof(StripeAnnualPriceId), ProviderIdMaxLength);
        PayPalMonthlyPlanId = Text.Optional(payPalMonthlyPlanId, nameof(PayPalMonthlyPlanId), ProviderIdMaxLength);
        PayPalAnnualPlanId = Text.Optional(payPalAnnualPlanId, nameof(PayPalAnnualPlanId), ProviderIdMaxLength);
    }

    public string? ProviderPriceId(PaymentProvider provider, BillingInterval interval) => (provider, interval) switch
    {
        (PaymentProvider.Stripe, BillingInterval.Monthly) => StripeMonthlyPriceId,
        (PaymentProvider.Stripe, BillingInterval.Annual) => StripeAnnualPriceId,
        (PaymentProvider.PayPal, BillingInterval.Monthly) => PayPalMonthlyPlanId,
        (PaymentProvider.PayPal, BillingInterval.Annual) => PayPalAnnualPlanId,
        _ => null,
    };

    private static string ValidCode(string code)
    {
        var value = Text.Required(code, nameof(Code), CodeMaxLength).ToLowerInvariant();
        if (!value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            throw new DomainException("Plan code may contain only lowercase letters, digits and hyphens.");
        }

        return value;
    }
}

/// <summary>Numeric plan limits. <c>null</c> means unlimited (or negotiated, for Enterprise).</summary>
public sealed record PlanLimits(
    int? MaxDatabaseConnections,
    int? MaxUsers,
    int? MaxConversationsPerDay,
    int? MaxApprovedTools,
    int? MaxToolExecutionsPerMonth)
{
    public static PlanLimits None { get; } = new(0, 0, 0, 0, 0);

    public void Validate()
    {
        if (new[] { MaxDatabaseConnections, MaxUsers, MaxConversationsPerDay, MaxApprovedTools, MaxToolExecutionsPerMonth }.Any(v => v < 0))
        {
            throw new DomainException("Plan limits cannot be negative.");
        }
    }
}
