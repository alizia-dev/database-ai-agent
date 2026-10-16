using System.Threading.RateLimiting;

namespace DatabaseAIAgent.Api.Configuration;

internal static class RateLimitingExtensions
{
    /// <summary>
    /// A global per-IP limiter for all requests, plus stricter named policies for credential and email-sending
    /// endpoints (brute-force and email-bombing protection). All windows are per client IP.
    /// </summary>
    public static IServiceCollection AddPlatformRateLimiting(this IServiceCollection services, ApiSecurityOptions security)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                PerIp(context, "global", security.RateLimitPermitsPerMinute));
            options.AddPolicy(RateLimitPolicies.Auth, context => PerIp(context, RateLimitPolicies.Auth, security.AuthRateLimitPermitsPerMinute));
            options.AddPolicy(RateLimitPolicies.Refresh, context => PerIp(context, RateLimitPolicies.Refresh, security.RefreshRateLimitPermitsPerMinute));
            options.AddPolicy(RateLimitPolicies.ConnectionTest, context => PerIp(context, RateLimitPolicies.ConnectionTest, security.ConnectionTestRateLimitPermitsPerMinute));
            options.AddPolicy(RateLimitPolicies.ToolExecution, context => PerUser(context, RateLimitPolicies.ToolExecution, security.ToolExecutionRateLimitPermitsPerMinute));
        });

        return services;
    }

    private static RateLimitPartition<string> PerUser(HttpContext context, string scope, int permitsPerMinute)
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
        return userId is null
            ? PerIp(context, scope, permitsPerMinute)
            : RateLimitPartition.GetFixedWindowLimiter($"{scope}:user:{userId}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }

    private static RateLimitPartition<string> PerIp(HttpContext context, string scope, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{scope}:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
