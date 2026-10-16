using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Jobs;
using DatabaseAIAgent.Domain.Organizations;
using DatabaseAIAgent.Domain.Users;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DatabaseAIAgent.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the platform database. It stores platform state and customer-database
/// <em>metadata</em> only; transactional customer data is never copied here.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : IdentityDbContext<User, Role, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "platform";

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<UserInvitation> UserInvitations => Set<UserInvitation>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<BillingAccount> BillingAccounts => Set<BillingAccount>();

    public DbSet<DatabaseConnection> DatabaseConnections => Set<DatabaseConnection>();

    public DbSet<DatabaseSchema> DatabaseSchemas => Set<DatabaseSchema>();

    public DbSet<DatabaseTable> DatabaseTables => Set<DatabaseTable>();

    public DbSet<DatabaseRelationship> DatabaseRelationships => Set<DatabaseRelationship>();

    public DbSet<StoredProcedure> StoredProcedures => Set<StoredProcedure>();

    public DbSet<SchemaAnalysis> SchemaAnalyses => Set<SchemaAnalysis>();

    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.HasDefaultSchema(Schema);

        builder.Entity<User>().ToTable("users");
        builder.Entity<Role>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<DataProtectionKey>().ToTable("data_protection_keys");

        builder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite (used only by the automated tests) cannot sort or compare DateTimeOffset values; store them as
        // UTC ticks there. PostgreSQL keeps native "timestamp with time zone" columns.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
            configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        }
    }
}
