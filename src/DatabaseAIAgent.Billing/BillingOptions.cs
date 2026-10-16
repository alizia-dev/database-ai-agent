using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Billing;

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    /// <summary>
    /// Identifies this deployment in payment-provider metadata (e.g. "acme-prod"). Required when a provider is enabled:
    /// webhook events carrying a different reference belong to another deployment and are ignored.
    /// </summary>
    [MaxLength(64)]
    public string AccountReference { get; set; } = string.Empty;

    /// <summary>Trial length for a workspace's first subscription; 0 disables trials.</summary>
    [Range(0, 90)]
    public int TrialDays { get; set; } = 7;

    /// <summary>Whether a payment method must be collected to start a trial (Stripe).</summary>
    public bool TrialRequiresPaymentMethod { get; set; } = true;

    /// <summary>How long access continues after a renewal payment fails.</summary>
    [Range(0, 60)]
    public int PastDueGraceDays { get; set; } = 7;

    /// <summary>
    /// Allows plan definitions (limits, features, prices, provider IDs) to be edited through the API. Enable only in
    /// operator-managed deployments: a customer Owner must not be able to raise their own plan limits.
    /// </summary>
    public bool AllowPlanEditing { get; set; }

    [EmailAddress]
    public string? SalesContactEmail { get; set; }

    /// <summary>Plan catalogue seeded into the database on first start (existing plans are not overwritten).</summary>
    public List<PlanSeed> Plans { get; set; } = [];
}

public sealed class PlanSeed
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public bool IsPublic { get; set; } = true;

    public bool IsFree { get; set; }

    public bool IsCustomPricing { get; set; }

    public string Currency { get; set; } = "USD";

    public decimal? MonthlyPrice { get; set; }

    public decimal? AnnualPrice { get; set; }

    public int? MaxDatabaseConnections { get; set; }

    public int? MaxUsers { get; set; }

    public int? MaxConversationsPerDay { get; set; }

    public int? MaxApprovedTools { get; set; }

    public int? MaxToolExecutionsPerMonth { get; set; }

    public List<string> Features { get; set; } = [];

    public List<string> Highlights { get; set; } = [];

    public string? StripeProductId { get; set; }

    public string? StripeMonthlyPriceId { get; set; }

    public string? StripeAnnualPriceId { get; set; }

    public string? PayPalMonthlyPlanId { get; set; }

    public string? PayPalAnnualPlanId { get; set; }
}

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    /// <summary>Secret API key (<c>STRIPE_SECRET_KEY</c>). Never exposed to clients.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Signing secret of this deployment's webhook endpoint (<c>STRIPE_WEBHOOK_SECRET</c>).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Maximum webhook age accepted (replay protection).</summary>
    [Range(30, 900)]
    public int WebhookToleranceSeconds { get; set; } = 300;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(WebhookSecret);
}

public enum PayPalMode
{
    Sandbox = 0,
    Live = 1,
}

public sealed class PayPalOptions
{
    public const string SectionName = "PayPal";

    public PayPalMode Mode { get; set; } = PayPalMode.Sandbox;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The webhook ID from the PayPal developer dashboard, required to verify webhook signatures.</summary>
    public string WebhookId { get; set; } = string.Empty;

    public string BrandName { get; set; } = "Database AI Agent";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(WebhookId);

    internal Uri ApiBase => Mode == PayPalMode.Live ? new Uri("https://api-m.paypal.com/") : new Uri("https://api-m.sandbox.paypal.com/");

    internal string ManagementUrl => Mode == PayPalMode.Live
        ? "https://www.paypal.com/myaccount/autopay/"
        : "https://www.sandbox.paypal.com/myaccount/autopay/";
}
