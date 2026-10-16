using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(DatabaseAIAgent.Api.Tests.ApiFactory))]

namespace DatabaseAIAgent.Api.Tests;

/// <summary>
/// Hosts the real API pipeline in memory. Shared across the test assembly because the Serilog bootstrap
/// logger can only be frozen once per process.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string AllowedOrigin = "https://app.example.test";
    public const string ThrowPath = "/__test/throw";
    public const string SensitiveExceptionText = "Password=SuperSecret123 relation \"customers\" does not exist";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        builder.UseSetting("ApiSecurity:AllowedOrigins:0", AllowedOrigin);
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, ThrowingEndpointStartupFilter>());
    }

    /// <summary>
    /// Appends a terminal middleware after the application pipeline that throws for <see cref="ThrowPath"/>,
    /// so the real exception handler is exercised with an exception carrying sensitive text.
    /// </summary>
    private sealed class ThrowingEndpointStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use((HttpContext context, RequestDelegate nextDelegate) =>
                context.Request.Path == ThrowPath
                    ? throw new InvalidOperationException(SensitiveExceptionText)
                    : nextDelegate(context));
        };
    }
}
