using DatabaseAIAgent.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DatabaseAIAgent.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organization", table =>
            // Single-tenant: the singleton row is the only row that may ever exist.
            table.HasCheckConstraint("ck_organization_singleton", $"\"Id\" = '{Organization.SingletonId}'"));

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.Name).HasMaxLength(Organization.NameMaxLength).IsRequired();
        builder.Property(o => o.Industry).HasMaxLength(Organization.IndustryMaxLength);
        builder.Property(o => o.AssistantName).HasMaxLength(Organization.AssistantNameMaxLength).IsRequired();
        builder.Property(o => o.BusinessDescription).HasMaxLength(Organization.BusinessDescriptionMaxLength);
        builder.Property(o => o.TimeZone).HasMaxLength(Organization.TimeZoneMaxLength).IsRequired();
        builder.Property(o => o.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        builder.Property(o => o.DateFormat).HasMaxLength(Organization.DateFormatMaxLength).IsRequired();
        builder.Property(o => o.ResponseStyle).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.ComplexProperty(o => o.Retention, retention =>
        {
            retention.Property(r => r.ConversationDays).HasColumnName("RetentionConversationDays");
            retention.Property(r => r.AuditLogDays).HasColumnName("RetentionAuditLogDays");
            retention.Property(r => r.UsageRecordDays).HasColumnName("RetentionUsageRecordDays");
            retention.Property(r => r.KnowledgeDocumentDays).HasColumnName("RetentionKnowledgeDocumentDays");
        });
    }
}
