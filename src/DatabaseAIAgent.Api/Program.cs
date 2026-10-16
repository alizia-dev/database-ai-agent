using System.Globalization;
using System.Threading.RateLimiting;
using DatabaseAIAgent.Agent;
using DatabaseAIAgent.Api.Configuration;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Api.Middleware;
using DatabaseAIAgent.Application;
using DatabaseAIAgent.Billing;
using DatabaseAIAgent.Database;
using DatabaseAIAgent.Infrastructure;
using DatabaseAIAgent.Infrastructure.Persistence;
using DatabaseAIAgent.Rag;
using DatabaseAIAgent.Security;
using DatabaseAIAgent.Tools;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, logger) => logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithProperty("Application", ObservabilityExtensions.ServiceName));

    builder.Services.AddOptions<ApiSecurityOptions>()
        .Bind(builder.Configuration.GetSection(ApiSecurityOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(o => o.AllowedOrigins.All(origin => Uri.TryCreate(origin, UriKind.Absolute, out _) && !origin.Contains('*', StringComparison.Ordinal)),
            "ApiSecurity:AllowedOrigins must contain absolute origins without wildcards.")
        .ValidateOnStart();
    var security = builder.Configuration.GetSection(ApiSecurityOptions.SectionName).Get<ApiSecurityOptions>() ?? new ApiSecurityOptions();

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.AddServerHeader = false;
        kestrel.Limits.MaxRequestBodySize = security.MaxRequestBodyBytes;
    });

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddSecurityModule()
        .AddBillingModule()
        .AddDatabaseModule()
        .AddToolsModule()
        .AddRagModule()
        .AddAgentModule();

    builder.Services.AddControllers();
    builder.Services.AddOpenApi();
    builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["correlationId"] = CorrelationIdMiddleware.Get(context.HttpContext);
        context.ProblemDetails.Extensions.Remove("traceId");
    });
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins(security.AllowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = security.RateLimitPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
    });

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // Trusted proxies/networks are configured per deployment (ForwardedHeaders:KnownNetworks); none are trusted by default.
    });

    builder.Services.AddPlatformHealthChecks(builder.Configuration);
    builder.Services.AddPlatformObservability(builder.Configuration);

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging(options => options.GetLevel = (context, _, exception) =>
        exception is not null || context.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
        : context.Request.Path.StartsWithSegments("/health") ? Serilog.Events.LogEventLevel.Verbose
        : Serilog.Events.LogEventLevel.Information);
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseMiddleware<SecurityHeadersMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }
    else
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }

    app.UseCors();
    app.UseRateLimiter();

    app.MapControllers();
    app.MapPlatformHealthChecks();

    if (!app.Environment.IsEnvironment("Testing"))
    {
        await PlatformDatabaseInitializer.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API host terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker for integration tests (WebApplicationFactory).</summary>
public partial class Program;
