using DatabaseAIAgent.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DatabaseAIAgent.ArchitectureTests;

/// <summary>Every registered service in the Workers host must be constructible (no missing dependencies).</summary>
public sealed class HostCompositionTests
{
    [Fact]
    public void WorkerServices_AllRegistrationsResolve()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Platform"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
                ["App:PublicUrl"] = "https://app.example.test",
                ["Email:Transport"] = "PickupDirectory",
                ["Email:FromAddress"] = "no-reply@example.test",
                ["Encryption:Key"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddWorkerServices(configuration, new TestEnvironment());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "DatabaseAIAgent.Workers";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
