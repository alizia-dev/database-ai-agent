using System.Collections.Concurrent;
using DatabaseAIAgent.Application.Abstractions.Email;
using DatabaseAIAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: AssemblyFixture(typeof(DatabaseAIAgent.Api.Tests.ApiFactory))]

namespace DatabaseAIAgent.Api.Tests;

/// <summary>
/// Hosts the real API pipeline in memory against a temporary SQLite platform database (schema created from the EF
/// model and seeded by the real initializer; WAL mode so parallel requests use separate connections). Outgoing email
/// is captured instead of sent. One host is shared by the whole test assembly (the Serilog bootstrap logger can only
/// be frozen once per process), so tests use unique emails and must not depend on each other's data.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AllowedOrigin = "https://app.example.test";
    public const string PublicAppUrl = "https://app.example.test";
    public const string ThrowPath = "/__test/throw";
    public const string SensitiveExceptionText = "Password=SuperSecret123 relation \"customers\" does not exist";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"daa-api-tests-{Guid.NewGuid():N}.db");

    public CapturingEmailSender Emails { get; } = new();

    private string ConnectionString => $"Data Source={_databasePath};Default Timeout=30";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        builder.UseSetting("ApiSecurity:AllowedOrigins:0", AllowedOrigin);
        // Every test request comes from the same in-memory client address, so limits are raised for the shared host.
        builder.UseSetting("ApiSecurity:AuthRateLimitPermitsPerMinute", "10000");
        builder.UseSetting("ApiSecurity:RefreshRateLimitPermitsPerMinute", "10000");
        builder.UseSetting("ApiSecurity:RateLimitPermitsPerMinute", "100000");
        builder.UseSetting("App:PublicUrl", PublicAppUrl);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
        builder.UseSetting("Email:Transport", "PickupDirectory");
        builder.UseSetting("Email:FromAddress", "no-reply@example.test");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<PlatformDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>();
            services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(ConnectionString));

            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);

            services.AddSingleton<IStartupFilter, ThrowingEndpointStartupFilter>();
        });
    }

    /// <summary>Starts the single shared host before any test runs (tests create clients in parallel).</summary>
    public async ValueTask InitializeAsync()
    {
        // Create the schema before the host starts so startup services (e.g. the data-protection key ring) find it.
        await using (var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(ConnectionString).Options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        }

        _ = Server;
        await PlatformDatabaseInitializer.InitializeAsync(Services);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // Best effort: the OS temp directory is cleaned eventually.
            }
        }
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

/// <summary>Records outgoing email so tests can follow verification, reset and invitation links.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string email) =>
        Sent.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Extracts the query parameters of the action link in the most recent email to <paramref name="email"/>.</summary>
    public Dictionary<string, string> LatestLinkQuery(string email, string path)
    {
        var message = To(email).LastOrDefault(m => m.TextBody.Contains($"/{path}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No '{path}' email was sent to {email}.");
        var url = message.TextBody.Split('\n').Select(l => l.Trim()).First(l => l.Contains($"/{path}", StringComparison.Ordinal));
        var uri = new Uri(url[url.IndexOf("http", StringComparison.Ordinal)..]);
        return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    }
}
