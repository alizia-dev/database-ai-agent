using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DatabaseAIAgent.Infrastructure.Persistence.Configurations;

internal sealed class DatabaseConnectionConfiguration : IEntityTypeConfiguration<DatabaseConnection>
{
    public void Configure(EntityTypeBuilder<DatabaseConnection> builder)
    {
        builder.ToTable("database_connections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(DatabaseConnection.NameMaxLength).IsRequired();
        builder.HasIndex(c => c.Name).IsUnique();
        builder.Property(c => c.Provider).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.Host).HasMaxLength(DatabaseConnection.HostMaxLength).IsRequired();
        builder.Property(c => c.DatabaseName).HasMaxLength(DatabaseConnection.DatabaseNameMaxLength).IsRequired();
        builder.Property(c => c.TlsMode).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.EncryptedCredentials).HasMaxLength(4096).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.LastFailure).HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.ServerVersion).HasMaxLength(DatabaseConnection.ServerVersionMaxLength);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.ComplexProperty(c => c.Permissions, p =>
        {
            p.Property(x => x.IsAdministrator).HasColumnName("PermIsAdministrator");
            p.Property(x => x.CanReadTablesDirectly).HasColumnName("PermCanReadTables");
            p.Property(x => x.CanWriteData).HasColumnName("PermCanWriteData");
            p.Property(x => x.ExecutableProcedureCount).HasColumnName("PermExecutableProcedures");
        });
    }
}
