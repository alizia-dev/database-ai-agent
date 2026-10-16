using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Billing;
using DatabaseAIAgent.Billing.Plans;
using DatabaseAIAgent.Billing.Subscriptions;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Common;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DatabaseAIAgent.UnitTests.Billing;

public sealed class SubscriptionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    [Fact]
    public void Apply_IgnoresEventsOlderThanTheLastAppliedOne()
    {
        var subscription = NewSubscription();
        Assert.True(subscription.Apply(Snapshot(SubscriptionStatus.Active, Now)));

        var applied = subscription.Apply(Snapshot(SubscriptionStatus.Canceled, Now.AddMinutes(-5)));

        Assert.False(applied);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.Trialing, true)]
    [InlineData(SubscriptionStatus.Incomplete, false)]
    [InlineData(SubscriptionStatus.Unpaid, false)]
    [InlineData(SubscriptionStatus.Canceled, false)]
    [InlineData(SubscriptionStatus.IncompleteExpired, false)]
    [InlineData(SubscriptionStatus.Paused, false)]
    public void GrantsAccess_DependsOnStatus(SubscriptionStatus status, bool expected)
    {
        var subscription = NewSubscription();
        subscription.Apply(Snapshot(status, Now));

        Assert.Equal(expected, subscription.GrantsAccess(Now, Grace));
    }

    [Fact]
    public void PastDue_KeepsAccessOnlyDuringTheGracePeriod()
    {
        var subscription = NewSubscription();
        subscription.Apply(Snapshot(SubscriptionStatus.Active, Now));
        subscription.Apply(Snapshot(SubscriptionStatus.PastDue, Now.AddDays(1)));
        subscription.Apply(Snapshot(SubscriptionStatus.PastDue, Now.AddDays(3))); // still the same past-due episode

        Assert.True(subscription.GrantsAccess(Now.AddDays(7), Grace));
        Assert.False(subscription.GrantsAccess(Now.AddDays(8).AddMinutes(1), Grace));

        subscription.Apply(Snapshot(SubscriptionStatus.Active, Now.AddDays(9)));
        Assert.Null(subscription.PastDueSince);
    }

    [Fact]
    public void Plan_RejectsUnknownFeaturesAndNegativeValues()
    {
        var plan = new Plan("pro", "Pro", 1);

        Assert.Throws<DomainException>(() => plan.SetEntitlements(new PlanLimits(1, 1, 1, 1, 1), ["teleportation"], []));
        Assert.Throws<DomainException>(() => plan.SetEntitlements(new PlanLimits(-1, 1, 1, 1, 1), [], []));
        Assert.Throws<DomainException>(() => plan.SetPricing(false, false, "USD", -5, null));
        Assert.Throws<DomainException>(() => new Plan("Bad Code!", "x", 1));
    }

    [Fact]
    public void Invoice_KeepsOnlyHttpsLinks()
    {
        var invoice = new Invoice(PaymentProvider.Stripe, "in_1");

        invoice.Update(new InvoiceSnapshot("1", InvoiceStatus.Paid, "usd", 10, 10, null, null, Now, Now, "javascript:alert(1)", "http://insecure.example/pdf"), null);

        Assert.Null(invoice.HostedInvoiceUrl);
        Assert.Null(invoice.PdfUrl);
        Assert.Equal("USD", invoice.Currency);
    }

    [Fact]
    public void Payment_RefundStatusFollowsAmount()
    {
        var payment = new Payment(PaymentProvider.Stripe, "pi_1");
        payment.Record(PaymentStatus.Succeeded, "usd", 100, null, Now, null);

        payment.RecordRefund(40);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);

        payment.RecordRefund(100);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    [Fact]
    public async Task Entitlements_FallBackToFreePlanWithoutAccessGrantingSubscription()
    {
        var free = Seeded("free", isFree: true, features: [PlanFeatures.DemoEnvironment]);
        var pro = Seeded("professional", features: [PlanFeatures.ProductionAccess, PlanFeatures.Rag]);
        var subscription = new Subscription(pro.Id, PaymentProvider.Stripe, "sub_1", "cus_1", BillingInterval.Monthly);
        subscription.Apply(Snapshot(SubscriptionStatus.PastDue, Now.AddDays(-30), pro.Id));

        var entitlements = await CreateService(subscription, free, pro).GetCurrentAsync(CancellationToken.None);

        Assert.Equal("free", entitlements.PlanCode);
        Assert.False(entitlements.HasProductionAccess);
        Assert.True(entitlements.PaymentPastDue);
    }

    [Fact]
    public async Task Entitlements_ComeFromThePaidPlanWhileActive()
    {
        var free = Seeded("free", isFree: true, features: [PlanFeatures.DemoEnvironment]);
        var pro = Seeded("professional", features: [PlanFeatures.ProductionAccess, PlanFeatures.Rag]);
        var subscription = new Subscription(pro.Id, PaymentProvider.Stripe, "sub_1", "cus_1", BillingInterval.Monthly);
        subscription.Apply(Snapshot(SubscriptionStatus.Trialing, Now.AddDays(-1), pro.Id) with { TrialEnd = Now.AddDays(6) });

        var entitlements = await CreateService(subscription, free, pro).GetCurrentAsync(CancellationToken.None);

        Assert.Equal("professional", entitlements.PlanCode);
        Assert.True(entitlements.HasProductionAccess);
        Assert.True(entitlements.HasFeature(PlanFeatures.Rag));
        Assert.Equal(Now.AddDays(6), entitlements.TrialEndsAt);
    }

    [Fact]
    public void AnnualSavings_AreComputedFromCataloguePrices()
    {
        var plan = BillingDataSeeder.Create(new PlanSeed { Code = "professional", Name = "Professional", MonthlyPrice = 149, AnnualPrice = 1430, Features = [PlanFeatures.ProductionAccess] });
        var catalog = new PlanCatalogService(Substitute.For<IPlanRepository>(), [], Substitute.For<IUnitOfWork>(), Options.Create(new BillingOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanCatalogService>.Instance);

        Assert.Equal(20, catalog.ToDto(plan).AnnualSavingsPercent);
    }

    [Fact]
    public async Task Seeder_AddsMissingPlansAndFillsEmptyProviderIdsWithoutOverwriting()
    {
        var existing = BillingDataSeeder.Create(new PlanSeed { Code = "starter", Name = "Edited by operator", StripeMonthlyPriceId = "price_operator" });
        var plans = Substitute.For<IPlanRepository>();
        plans.ListAsync(Arg.Any<CancellationToken>()).Returns([existing]);
        var accounts = Substitute.For<IBillingAccountRepository>();
        accounts.ExistsAsync(Arg.Any<CancellationToken>()).Returns(false);
        var options = Options.Create(new BillingOptions
        {
            Plans =
            [
                new PlanSeed { Code = "starter", Name = "Starter", StripeMonthlyPriceId = "price_config", StripeAnnualPriceId = "price_config_annual" },
                new PlanSeed { Code = "business", Name = "Business" },
            ],
        });

        await new BillingDataSeeder(plans, accounts, Substitute.For<IUnitOfWork>(), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<BillingDataSeeder>.Instance)
            .SeedAsync(CancellationToken.None);

        Assert.Equal("Edited by operator", existing.Name);
        Assert.Equal("price_operator", existing.StripeMonthlyPriceId);
        Assert.Equal("price_config_annual", existing.StripeAnnualPriceId);
        plans.Received(1).Add(Arg.Is<Plan>(p => p.Code == "business"));
        accounts.Received(1).Add(Arg.Any<BillingAccount>());
    }

    private static EntitlementService CreateService(Subscription? subscription, params Plan[] catalogue)
    {
        var subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(subscription);
        var plans = Substitute.For<IPlanRepository>();
        plans.ListAsync(Arg.Any<CancellationToken>()).Returns(catalogue);
        plans.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => catalogue.FirstOrDefault(p => p.Id == call.Arg<Guid>()));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        return new EntitlementService(subscriptions, plans, clock, Options.Create(new BillingOptions { PastDueGraceDays = 7 }));
    }

    private static Plan Seeded(string code, bool isFree = false, string[]? features = null) =>
        BillingDataSeeder.Create(new PlanSeed { Code = code, Name = code, IsFree = isFree, MonthlyPrice = isFree ? 0 : 10, Features = [.. features ?? []] });

    private static Subscription NewSubscription() => new(Guid.NewGuid(), PaymentProvider.Stripe, "sub_1", "cus_1", BillingInterval.Monthly);

    private static SubscriptionSnapshot Snapshot(SubscriptionStatus status, DateTimeOffset at, Guid? planId = null) =>
        new(planId ?? Guid.NewGuid(), BillingInterval.Monthly, status, "cus_1", at, at.AddMonths(1), null, false, null, at);
}
