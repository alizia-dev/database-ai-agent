using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;
using DatabaseAIAgent.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DatabaseAIAgent.Infrastructure.Persistence.Configurations;

internal sealed class AiToolConfiguration : IEntityTypeConfiguration<AiTool>
{
    public void Configure(EntityTypeBuilder<AiTool> builder)
    {
        builder.ToTable("ai_tools");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(AiTool.NameMaxLength).IsRequired();
        builder.HasIndex(t => t.Name).IsUnique();
        builder.HasIndex(t => t.StoredProcedureId).IsUnique();
        builder.Property(t => t.Description).HasMaxLength(AiTool.DescriptionMaxLength).IsRequired();
        builder.Property(t => t.Operation).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Risk).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.ClassificationSource).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.DisabledReason).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.UnsupportedReason).HasMaxLength(500);
        builder.Property(t => t.RejectionReason).HasMaxLength(500);
        builder.Property(t => t.SourceDefinitionHash).HasMaxLength(64);
        builder.HasOne<DatabaseConnection>().WithMany().HasForeignKey(t => t.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoredProcedure>().WithMany().HasForeignKey(t => t.StoredProcedureId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.ApprovedByUserId).OnDelete(DeleteBehavior.SetNull);
        builder.Ignore(t => t.IsAvailable);
        builder.Ignore(t => t.RequiresConfirmation);

        builder.OwnsMany(t => t.Parameters, p => p.ToJson());
        builder.OwnsMany(t => t.AllowedRoles, r =>
        {
            r.ToTable("tool_permissions");
            r.WithOwner().HasForeignKey("ToolId");
            r.Property<int>("Id");
            r.HasKey("Id");
            r.Property(x => x.Role).HasMaxLength(32).IsRequired();
        });
    }
}

internal sealed class ToolExecutionConfiguration : IEntityTypeConfiguration<ToolExecution>
{
    public void Configure(EntityTypeBuilder<ToolExecution> builder)
    {
        builder.ToTable("tool_executions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ReasonCode).HasMaxLength(64);
        builder.Property(e => e.RedactedArguments).HasMaxLength(4000);
        builder.HasIndex(e => new { e.ToolId, e.StartedAt });
        builder.HasIndex(e => e.StartedAt);
        builder.HasOne<AiTool>().WithMany().HasForeignKey(e => e.ToolId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
