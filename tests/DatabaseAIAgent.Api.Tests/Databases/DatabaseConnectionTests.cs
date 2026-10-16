using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Api.Tests.Databases;

public sealed class DatabaseConnectionTests(ApiFactory factory)
{
    // TEST-NET-3 (RFC 5737): a public-looking documentation address, accepted by the host guard without DNS.
    private const string PublicHost = "203.0.113.10";
    private const string Password = "s3cret-Pa55word-value";

    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Providers_ListAllEnginesWithLeastPrivilegeScripts()
    {
        var owner = await _api.OwnerAsync();

        var providers = await Json(await Send(HttpMethod.Get, "/api/database/providers", owner.AccessToken));

        Assert.Equal(["SqlServer", "PostgreSql", "MySql"], providers.EnumerateArray().Select(p => p.GetProperty("kind").GetString()).ToArray());
        Assert.All(providers.EnumerateArray(), p => Assert.Contains("EXECUTE", p.GetProperty("leastPrivilegeSetupScript").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAPaidPlan_ConnectingIsRefused()
    {
        var owner = await _api.OwnerAsync();

        var response = await Send(HttpMethod.Post, "/api/database/connections/test", owner.AccessToken, Input("free-plan"), paidPlan: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("billing.upgrade_required", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task MembersWithoutDatabaseManage_CannotTestOrCreate()
    {
        var analyst = await _api.MemberAsync(SystemRoles.Analyst);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/database/connections/test", analyst.AccessToken, Input("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/api/database/connections", analyst.AccessToken, Input("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/database/connections", analyst.AccessToken)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")] // loopback
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("10.0.0.5")] // private network (not allowed in this deployment)
    [InlineData("0.0.0.0")]
    public async Task Test_RejectsInternalTargetsBeforeConnecting(string host)
    {
        var owner = await _api.OwnerAsync();
        var before = factory.Postgres.Tested.Count;

        var result = await Json(await Send(HttpMethod.Post, "/api/database/connections/test", owner.AccessToken, Input("ssrf", host: host)));

        Assert.False(result.GetProperty("succeeded").GetBoolean());
        Assert.Equal("HostNotAllowed", result.GetProperty("failure").GetString());
        Assert.Equal(before, factory.Postgres.Tested.Count);
    }

    [Theory]
    [InlineData("db.example.com;Integrated Security=true")]
    [InlineData("https://db.example.com")]
    [InlineData("db.example.com\\SQLEXPRESS")]
    public async Task Test_RejectsHostsThatAreNotPlainHostNames(string host)
    {
        var owner = await _api.OwnerAsync();

        var response = await Send(HttpMethod.Post, "/api/database/connections/test", owner.AccessToken, Input("bad-host", host: host));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"host\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PastedConnectionString_IsParsedAndUnsupportedOptionsAreReported()
    {
        var owner = await _api.OwnerAsync();
        var input = new
        {
            name = "pasted",
            provider = "PostgreSql",
            connectionString = $"Host={PublicHost};Port=6543;Database=sales;Username=reporting;Password={Password};SSL Mode=Require;Include Error Detail=true",
        };

        var result = await Json(await Send(HttpMethod.Post, "/api/database/connections/test", owner.AccessToken, input));

        Assert.True(result.GetProperty("succeeded").GetBoolean());
        // Keys are reported as normalized by the driver's parser (lower case).
        Assert.Contains("include error detail", result.GetProperty("ignoredOptions").EnumerateArray().Select(o => o.GetString()), StringComparer.OrdinalIgnoreCase);
        var tested = factory.Postgres.Tested.Last();
        Assert.Equal((PublicHost, 6543, "sales", "reporting"), (tested.Host, tested.Port, tested.Database, tested.Username));
        Assert.Equal(DatabaseAIAgent.Domain.Databases.TlsMode.Required, tested.TlsMode);
    }

    [Fact]
    public async Task ConnectionLifecycle_CredentialsAreEncryptedAndNeverReturned()
    {
        var owner = await _api.OwnerAsync();
        var name = $"Sales {Guid.NewGuid():N}";

        // A failed test blocks saving.
        var rejected = await Send(HttpMethod.Post, "/api/database/connections", owner.AccessToken, Input(name, password: FakePostgresProvider.WrongPassword));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("database.connection_failed", await TestApi.ErrorCodeAsync(rejected));

        // Create: tested first, then stored with encrypted credentials.
        var created = await Send(HttpMethod.Post, "/api/database/connections", owner.AccessToken, Input(name));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(Password, createdBody, StringComparison.Ordinal);
        Assert.DoesNotContain("reporting_user", createdBody, StringComparison.Ordinal);
        var id = JsonDocument.Parse(createdBody).RootElement.GetProperty("connection").GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().DatabaseConnections.AsNoTracking().SingleAsync(c => c.Id == id, TestContext.Current.CancellationToken);
            Assert.StartsWith("v1.", row.EncryptedCredentials, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, row.EncryptedCredentials, StringComparison.Ordinal);
            Assert.DoesNotContain("reporting_user", row.EncryptedCredentials, StringComparison.Ordinal);
        }

        // Duplicate names are refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, "/api/database/connections", owner.AccessToken, Input(name.ToUpperInvariant()))).StatusCode);

        // Reads never include credentials.
        var fetched = await Send(HttpMethod.Get, $"/api/database/connections/{id}", owner.AccessToken);
        var fetchedBody = await fetched.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Connected", JsonDocument.Parse(fetchedBody).RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(Password, fetchedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("password", fetchedBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username", fetchedBody, StringComparison.OrdinalIgnoreCase);

        // Updating without a password keeps the stored credentials (decrypted server-side for the re-test).
        var updated = await Send(HttpMethod.Put, $"/api/database/connections/{id}", owner.AccessToken, new { name, provider = "PostgreSql", host = PublicHost, port = 5433, database = "sales_v2", tlsMode = "VerifyFull" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var retestedWith = factory.Postgres.Tested.Last();
        Assert.Equal(("reporting_user", Password, "sales_v2", 5433), (retestedWith.Username, retestedWith.Password, retestedWith.Database, retestedWith.Port));

        // Re-test with stored credentials.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/database/connections/{id}/test", owner.AccessToken)).StatusCode);
        Assert.Equal(Password, factory.Postgres.Tested.Last().Password);

        // Delete removes metadata and credentials.
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/database/connections/{id}", owner.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/database/connections/{id}", owner.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Create_EnforcesThePlanConnectionLimit()
    {
        var owner = await _api.OwnerAsync();
        var existing = (await Json(await Send(HttpMethod.Get, "/api/database/connections", owner.AccessToken))).GetArrayLength();

        var response = await Send(HttpMethod.Post, "/api/database/connections", owner.AccessToken, Input($"limit-{Guid.NewGuid():N}"), paidPlan: existing.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("billing.limit_reached", await TestApi.ErrorCodeAsync(response));
    }

    private static object Input(string name, string host = PublicHost, string password = Password) => new
    {
        name,
        provider = "PostgreSql",
        host,
        port = 5432,
        database = "sales",
        username = "reporting_user",
        password,
        tlsMode = "VerifyFull",
    };

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string accessToken, object? body = null, string? paidPlan = "")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (paidPlan is not null)
        {
            request.Headers.Add(TestEntitlements.Header, paidPlan.Length == 0 ? "unlimited" : paidPlan);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _api.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
