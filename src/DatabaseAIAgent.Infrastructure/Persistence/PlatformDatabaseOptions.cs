using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Infrastructure.Persistence;

/// <summary>
/// Settings for the platform database (PostgreSQL). The connection string is supplied through
/// configuration/secret injection (e.g. <c>ConnectionStrings__Platform</c>) and is never committed to source control.
/// </summary>
public sealed class PlatformDatabaseOptions
{
    public const string SectionName = "PlatformDatabase";

    public const string ConnectionStringName = "Platform";

    [Required]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Apply pending EF Core migrations at startup. Intended for local development only.</summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}
