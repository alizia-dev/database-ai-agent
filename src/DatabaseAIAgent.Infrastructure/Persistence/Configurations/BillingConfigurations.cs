using DatabaseAIAgent.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DatabaseAIAgent.Infrastructure.Persistence.Configurations;

internal static class MoneyColumn
{
    public const int Precision = 12;
    public const int Scale = 2;
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Code).HasMaxLength(Plan.CodeMaxLength).IsRequired();
        builder.HasIndex(p => p.Code).IsUnique();
        builder.Property(p => p.Name).HasMaxLength(Plan.NameMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Plan.DescriptionMaxLength);
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(p => p.MonthlyPrice).HasPrecision(MoneyColumn.Precision, MoneyColumn.Scale);
        builder.Property(p => p.AnnualPrice).HasPrecision(MoneyColumn.Precision, MoneyColumn.Scale);
        builder.Property(p => p.Features);
        builder.Property(p => p.Highlights);

        foreach (var providerId in new[] { nameof(Plan.StripeProductId), nameof(Plan.StripeMonthlyPriceId), nameof(Plan.StripeAnnualPriceId), nameof(Plan.PayPalMonthlyPlanId), nameof(Plan.PayPalAnnualPlanId) })
        {
            builder.Property<string?>(providerId).HasMaxLength(Plan.ProviderIdMaxLength);
        }

        builder.Ignore(p => p.IsPurchasable);
        builder.ComplexProperty(p => p.Limits, limits =>
        {
            limits.Property(l => l.MaxDatabaseConnections).HasColumnName("LimitDatabaseConnections");
            limits.Property(l => l.MaxUsers).HasColumnName("LimitUsers");
            limits.Property(l => l.MaxConversationsPerDay).HasColumnName("LimitConversationsPerDay");
            limits.Property(l => l.MaxApprovedTools).HasColumnName("LimitApprovedTools");
            limits.Property(l => l.MaxToolExecutionsPerMonth).HasColumnName("LimitToolExecutionsPerMonth");
        });
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Provider).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(s => s.Interval).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.ProviderSubscriptionId).HasMaxLength(Plan.ProviderIdMaxLength).IsRequired();
        builder.Property(s => s.ProviderCustomerId).HasMaxLength(Plan.ProviderIdMaxLength);
        builder.HasIndex(s => new { s.Provider, s.ProviderSubscriptionId }).IsUnique();
        builder.HasIndex(s => s.ProviderCustomerId);
        builder.HasOne<Plan>().WithMany().HasForeignKey(s => s.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(s => s.IsTerminal);
    }
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Provider).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(i => i.ProviderInvoiceId).HasMaxLength(Plan.ProviderIdMaxLength).IsRequired();
        builder.Property(i => i.Number).HasMaxLength(64);
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(i => i.AmountDue).HasPrecision(MoneyColumn.Precision, MoneyColumn.Scale);
        builder.Property(i => i.AmountPaid).HasPrecision(MoneyColumn.Precision, MoneyColumn.Scale);
        builder.Property(i => i.HostedInvoiceUrl).HasMaxLength(2048);
        builder.Property(i => i.PdfUrl).HasMaxLength(2048);
        builder.HasIndex(i => new { i.Provider, i.ProviderInvoiceId }).IsUnique();
        builder.HasIndex(i => i.IssuedAt);
        builder.HasOne<Subscription>().WithMany().HasForeignKey(i => i.SubscriptionId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Provider).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(24);
        builder.Property(p => p.ProviderPaymentId).HasMaxLength(Plan.ProviderIdMaxLength).IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength();
        builder.Property(p => p.Amount).HasPrecision(MoneyColumn.Precision, MoneyColumn.Scale);
        builder.Property(p => p.AmountRefunded).HasPrecision(MoneyColumn.Precision, MoneyColumn.Scale);
        builder.Property(p => p.FailureMessage).HasMaxLength(Payment.FailureMessageMaxLength);
        builder.HasIndex(p => new { p.Provider, p.ProviderPaymentId }).IsUnique();
        builder.HasIndex(p => p.OccurredAt);
        builder.HasOne<Invoice>().WithMany().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Provider).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.ProviderEventId).HasMaxLength(Plan.ProviderIdMaxLength).IsRequired();
        builder.Property(e => e.Type).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Payload).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(500);
        // Idempotency: one row per provider event, however often it is delivered.
        builder.HasIndex(e => new { e.Provider, e.ProviderEventId }).IsUnique();
        builder.HasIndex(e => new { e.Status, e.ReceivedAt });
        builder.Ignore(e => e.IsRetryable);
    }
}

internal sealed class BillingAccountConfiguration : IEntityTypeConfiguration<BillingAccount>
{
    public void Configure(EntityTypeBuilder<BillingAccount> builder)
    {
        builder.ToTable("billing_account", table =>
            table.HasCheckConstraint("ck_billing_account_singleton", $"\"Id\" = '{BillingAccount.SingletonId}'"));
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.StripeCustomerId).HasMaxLength(Plan.ProviderIdMaxLength);
        builder.Property(a => a.BillingEmail).HasMaxLength(256);
    }
}
