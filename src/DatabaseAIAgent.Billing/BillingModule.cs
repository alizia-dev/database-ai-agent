using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Billing.Plans;
using DatabaseAIAgent.Billing.Providers.PayPal;
using DatabaseAIAgent.Billing.Providers.Stripe;
using DatabaseAIAgent.Billing.Subscriptions;
using DatabaseAIAgent.Billing.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IStripeClient = Stripe.IStripeClient;
using StripeClient = Stripe.StripeClient;
using SystemNetHttpClient = Stripe.SystemNetHttpClient;

namespace DatabaseAIAgent.Billing;

/// <summary>Composition entry point for plans, subscriptions, entitlements and payment providers.</summary>
public static class BillingModule
{
    public const string StripeHttpClient = "stripe";

    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PayPalOptions>().Bind(configuration.GetSection(PayPalOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<BillingOptions>()
            .Bind(configuration.GetSection(BillingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate<IOptions<StripeOptions>, IOptions<PayPalOptions>>(
                (billing, stripe, payPal) => !(stripe.Value.IsConfigured || payPal.Value.IsConfigured) || !string.IsNullOrWhiteSpace(billing.AccountReference),
                "Billing:AccountReference is required when a payment provider is configured.")
            .ValidateOnStart();

        // Stripe: one client per process, with Stripe's own retry policy for transient network errors.
        services.AddHttpClient(StripeHttpClient);
        services.AddSingleton<IStripeClient>(provider =>
        {
            var key = provider.GetRequiredService<IOptions<StripeOptions>>().Value.SecretKey;
            var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(StripeHttpClient);
            // The placeholder is never used for requests: the provider reports IsEnabled=false without a key.
            return new StripeClient(string.IsNullOrWhiteSpace(key) ? "sk_not_configured" : key, httpClient: new SystemNetHttpClient(http, maxNetworkRetries: 2));
        });

        // PayPal: typed HttpClient with standard resilience (retries with backoff, circuit breaker, timeouts).
        services.AddHttpClient<PayPalClient>().AddStandardResilienceHandler();

        services.AddScoped<IPaymentProvider, StripePaymentProvider>();
        services.AddScoped<IPaymentProvider, PayPalPaymentProvider>();

        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<PlanCatalogService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<BillingEventApplier>();
        services.AddScoped<BillingWebhookService>();
        services.AddScoped<IDataSeeder, BillingDataSeeder>();

        return services;
    }

    /// <summary>Background reprocessing of failed webhooks (registered by the Workers host).</summary>
    public static IServiceCollection AddBillingWorkers(this IServiceCollection services)
    {
        services.AddHostedService<WebhookRetryService>();
        return services;
    }
}
