using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Api.Configuration;

public sealed class ApiSecurityOptions
{
    public const string SectionName = "ApiSecurity";

    /// <summary>Exact origins allowed by CORS (e.g. the Angular app). Wildcards are not permitted.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    [Range(1, 100_000)]
    public int RateLimitPermitsPerMinute { get; set; } = 300;

    /// <summary>Per-IP limit for login, registration, password reset, verification and invitation endpoints.</summary>
    [Range(1, 10_000)]
    public int AuthRateLimitPermitsPerMinute { get; set; } = 10;

    [Range(1, 10_000)]
    public int RefreshRateLimitPermitsPerMinute { get; set; } = 30;

    /// <summary>Per-IP limit for endpoints that open connections to customer databases.</summary>
    [Range(1, 10_000)]
    public int ConnectionTestRateLimitPermitsPerMinute { get; set; } = 10;

    /// <summary>Maximum request body in bytes. File uploads get their own, explicit limits.</summary>
    [Range(1_024, 100 * 1024 * 1024)]
    public long MaxRequestBodyBytes { get; set; } = 1 * 1024 * 1024;
}
