using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Jobs;
using DatabaseAIAgent.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DatabaseAIAgent.Infrastructure.Persistence.Configurations;

internal static class MetadataColumns
{
    public const int Identifier = 256;
}

internal sealed class DatabaseSchemaConfiguration : IEntityTypeConfiguration<DatabaseSchema>
{
    public void Configure(EntityTypeBuilder<DatabaseSchema> builder)
    {
        builder.ToTable("db_schemas");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(MetadataColumns.Identifier).IsRequired();
        builder.HasIndex(s => new { s.ConnectionId, s.Name }).IsUnique();
        builder.HasOne<DatabaseConnection>().WithMany().HasForeignKey(s => s.ConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DatabaseTableConfiguration : IEntityTypeConfiguration<DatabaseTable>
{
    public void Configure(EntityTypeBuilder<DatabaseTable> builder)
    {
        builder.ToTable("db_tables");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.SchemaName).HasMaxLength(MetadataColumns.Identifier).IsRequired();
        builder.Property(t => t.Name).HasMaxLength(MetadataColumns.Identifier).IsRequired();
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.Description).HasMaxLength(2000);
        builder.HasIndex(t => new { t.ConnectionId, t.SchemaName, t.Name }).IsUnique();
        builder.HasOne<DatabaseConnection>().WithMany().HasForeignKey(t => t.ConnectionId).OnDelete(DeleteBehavior.Cascade);

        // Child metadata is always read and replaced with its table, so it is stored as JSON on the row.
        builder.OwnsMany(t => t.Columns, c => c.ToJson());
        builder.OwnsMany(t => t.Indexes, i => i.ToJson());
    }
}

internal sealed class DatabaseRelationshipConfiguration : IEntityTypeConfiguration<DatabaseRelationship>
{
    public void Configure(EntityTypeBuilder<DatabaseRelationship> builder)
    {
        builder.ToTable("db_relationships");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).HasMaxLength(MetadataColumns.Identifier).IsRequired();
        builder.Property(r => r.FromSchema).HasMaxLength(MetadataColumns.Identifier);
        builder.Property(r => r.FromTable).HasMaxLength(MetadataColumns.Identifier);
        builder.Property(r => r.ToSchema).HasMaxLength(MetadataColumns.Identifier);
        builder.Property(r => r.ToTable).HasMaxLength(MetadataColumns.Identifier);
        builder.HasIndex(r => r.ConnectionId);
        builder.HasOne<DatabaseConnection>().WithMany().HasForeignKey(r => r.ConnectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class StoredProcedureConfiguration : IEntityTypeConfiguration<StoredProcedure>
{
    public void Configure(EntityTypeBuilder<StoredProcedure> builder)
    {
        builder.ToTable("db_procedures");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.SchemaName).HasMaxLength(MetadataColumns.Identifier).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(MetadataColumns.Identifier).IsRequired();
        builder.Property(p => p.Signature).HasMaxLength(1000).IsRequired();
        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.ReturnType).HasMaxLength(200);
        builder.Property(p => p.DeclaredDataAccess).HasMaxLength(64);
        builder.Property(p => p.DefinitionHash).HasMaxLength(64);
        builder.HasIndex(p => new { p.ConnectionId, p.SchemaName, p.Name, p.Signature }).IsUnique();
        builder.HasOne<DatabaseConnection>().WithMany().HasForeignKey(p => p.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.ComplexProperty(p => p.Behavior, b =>
        {
            b.Property(x => x.DefinitionAvailable).HasColumnName("DefinitionAvailable");
            b.Property(x => x.ModifiesData).HasColumnName("ModifiesData");
            b.Property(x => x.DeletesData).HasColumnName("DeletesData");
            b.Property(x => x.ChangesSchema).HasColumnName("ChangesSchema");
            b.Property(x => x.UsesDynamicSql).HasColumnName("UsesDynamicSql");
            b.Property(x => x.CallsOtherRoutines).HasColumnName("CallsOtherRoutines");
        });
        builder.OwnsMany(p => p.Parameters, x => x.ToJson());
        builder.OwnsMany(p => p.ResultColumns, x => x.ToJson());
    }
}

internal sealed class SchemaAnalysisConfiguration : IEntityTypeConfiguration<SchemaAnalysis>
{
    public void Configure(EntityTypeBuilder<SchemaAnalysis> builder)
    {
        builder.ToTable("schema_analyses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Failure).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(a => new { a.ConnectionId, a.QueuedAt });
        builder.HasOne<DatabaseConnection>().WithMany().HasForeignKey(a => a.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.RequestedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Ignore(a => a.IsActive);
    }
}

internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> builder)
    {
        builder.ToTable("background_jobs");
        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();
        builder.Property(j => j.Type).HasMaxLength(BackgroundJob.TypeMaxLength).IsRequired();
        builder.Property(j => j.Payload).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(j => j.LockedBy).HasMaxLength(128);
        builder.Property(j => j.LastError).HasMaxLength(BackgroundJob.ErrorMaxLength);
        builder.HasIndex(j => new { j.Status, j.AvailableAt });
    }
}
