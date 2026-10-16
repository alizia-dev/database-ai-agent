using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DatabaseAIAgent.Api.Configuration;

internal static class HealthCheckExtensions
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddPlatformHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        var checks = services.AddHealthChecks();

        var platformDb = configuration.GetConnectionString("Platform") ?? configuration["PlatformDatabase:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(platformDb))
        {
            checks.AddNpgSql(platformDb, name: "platform-database", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(5));
        }

        var redis = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            checks.AddRedis(redis, name: "redis", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(5));
        }

        return services;
    }

    /// <summary>
    /// <c>/health/live</c>: process is up (no dependencies). <c>/health/ready</c>: dependencies reachable.
    /// Responses expose only check names and statuses, never connection details or exception messages.
    /// </summary>
    public static void MapPlatformHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync })
            .DisableRateLimiting();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync })
            .DisableRateLimiting();
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
    }
}
