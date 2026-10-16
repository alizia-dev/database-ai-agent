using System.Globalization;
using DatabaseAIAgent.Workers;
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

    builder.Services.AddWorkerServices(builder.Configuration, builder.Environment);

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
