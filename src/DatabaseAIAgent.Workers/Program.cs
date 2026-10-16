using System.Globalization;
using DatabaseAIAgent.Application;
using DatabaseAIAgent.Billing;
using DatabaseAIAgent.Database;
using DatabaseAIAgent.Infrastructure;
using DatabaseAIAgent.Rag;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console(formatProvider: CultureInfo.InvariantCulture).CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, logger) => logger
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "database-ai-agent-workers"));

    // Long-running work (schema analysis, embeddings, document processing, usage aggregation, health checks,
    // webhook processing, cleanup) runs here, never inside an HTTP request. Job processors are registered as
    // their phases are implemented.
    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddBillingModule()
        .AddDatabaseModule()
        .AddRagModule();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker host terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
