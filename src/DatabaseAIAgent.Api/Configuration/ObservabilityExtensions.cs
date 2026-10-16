using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DatabaseAIAgent.Api.Configuration;

internal static class ObservabilityExtensions
{
    public const string ServiceName = "database-ai-agent-api";

    /// <summary>
    /// Registers OpenTelemetry tracing and metrics. Export uses OTLP and is enabled only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (or <c>OpenTelemetry:OtlpEndpoint</c>) is configured.
    /// </summary>
    public static IServiceCollection AddPlatformObservability(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? configuration["OpenTelemetry:OtlpEndpoint"];
        var exportEnabled = Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out _);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddSource("DatabaseAIAgent.*");
                if (exportEnabled)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("DatabaseAIAgent.*");
                if (exportEnabled)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }
}
