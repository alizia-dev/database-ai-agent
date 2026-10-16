using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Api.Tests.Databases;

public sealed class SchemaAnalysisTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Analysis_RunsInTheBackgroundAndPopulatesTheCatalog()
    {
        var owner = await _api.OwnerAsync();
        var connectionId = await CreateConnectionAsync(owner.AccessToken);
        factory.Discovery.Catalogs[connectionId] = FakeDiscovery.Crm("get_outstanding_invoices", "set_credit_limit");

        // The request only queues work.
        var started = await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", owner.AccessToken);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal("Queued", (await Json(started)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", owner.AccessToken)).StatusCode);

        await DrainJobsAsync();

        var overview = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/schema", owner.AccessToken));
        var analysis = overview.GetProperty("latestAnalysis");
        Assert.Equal("Succeeded", analysis.GetProperty("status").GetString());
        Assert.Equal(2, analysis.GetProperty("counts").GetProperty("tables").GetInt32());
        Assert.Equal(1, analysis.GetProperty("counts").GetProperty("views").GetInt32());
        Assert.Equal(2, analysis.GetProperty("counts").GetProperty("procedures").GetInt32());
        Assert.Contains("Example warning from discovery.", analysis.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        var publicSchema = overview.GetProperty("schemas").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "public");
        Assert.Equal(2, publicSchema.GetProperty("tables").GetInt32());

        // Tables: searchable, with columns, keys and relationships in both directions.
        var tables = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/tables?search=CUST", owner.AccessToken));
        var customersId = tables.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();
        var customers = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/tables/{customersId}", owner.AccessToken));
        Assert.Equal(["id", "name", "balance"], customers.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.True(customers.GetProperty("columns")[0].GetProperty("isPrimaryKey").GetBoolean());
        Assert.Equal("invoices", customers.GetProperty("referencedBy")[0].GetProperty("fromTable").GetString());

        // Procedures: parameters, result columns and behaviour from definition analysis.
        var procedures = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/procedures", owner.AccessToken));
        var byName = procedures.GetProperty("items").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("id").GetGuid());
        var reader = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/procedures/{byName["get_outstanding_invoices"]}", owner.AccessToken));
        var writer = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/procedures/{byName["set_credit_limit"]}", owner.AccessToken));
        Assert.True(reader.GetProperty("isReadOnly").GetBoolean());
        Assert.Equal("p_min_balance", reader.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.True(reader.GetProperty("parameters")[0].GetProperty("hasDefault").GetBoolean());
        Assert.Equal(2, reader.GetProperty("resultColumns").GetArrayLength());
        Assert.False(writer.GetProperty("isReadOnly").GetBoolean());
        Assert.True(writer.GetProperty("behavior").GetProperty("modifiesData").GetBoolean());

        // The routine source itself is never returned or stored, only a hash.
        var raw = await (await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/procedures/{byName["set_credit_limit"]}", owner.AccessToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("UPDATE customers", raw, StringComparison.OrdinalIgnoreCase);

        // Re-analysis keeps IDs of procedures that still exist and flags removed ones.
        factory.Discovery.Catalogs[connectionId] = FakeDiscovery.Crm("get_outstanding_invoices", "get_top_customers");
        Assert.Equal(HttpStatusCode.Accepted, (await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", owner.AccessToken)).StatusCode);
        await DrainJobsAsync();

        var after = await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/procedures?includeMissing=true", owner.AccessToken));
        var items = after.GetProperty("items").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        Assert.Equal(byName["get_outstanding_invoices"], items["get_outstanding_invoices"].GetProperty("id").GetGuid());
        Assert.True(items["set_credit_limit"].GetProperty("isMissing").GetBoolean());
        Assert.False(items["get_top_customers"].GetProperty("isMissing").GetBoolean());
        var latest = (await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/schema", owner.AccessToken))).GetProperty("latestAnalysis").GetProperty("counts");
        Assert.Equal((1, 1), (latest.GetProperty("newProcedures").GetInt32(), latest.GetProperty("missingProcedures").GetInt32()));
    }

    [Fact]
    public async Task UnreachableDatabase_FailsTheAnalysisWithASafeReason()
    {
        var owner = await _api.OwnerAsync();
        var connectionId = await CreateConnectionAsync(owner.AccessToken);
        factory.Discovery.Failures[connectionId] = ConnectionFailureKind.HostUnreachable;

        await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", owner.AccessToken);
        await DrainJobsAsync();

        var analysis = (await Json(await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/schema", owner.AccessToken))).GetProperty("latestAnalysis");
        Assert.Equal("Failed", analysis.GetProperty("status").GetString());
        Assert.Contains("could not be reached", analysis.GetProperty("failureMessage").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permissions_ViewRequiresDatabaseView_AnalyzeRequiresDatabaseAnalyze()
    {
        var owner = await _api.OwnerAsync();
        var connectionId = await CreateConnectionAsync(owner.AccessToken);
        var analyst = await _api.MemberAsync(SystemRoles.Analyst);
        var viewer = await _api.MemberAsync(SystemRoles.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/schema", analyst.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", analyst.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"/api/database/connections/{connectionId}/tables", viewer.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/database/connections/{Guid.NewGuid()}/schema", owner.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Analyze_RequiresAPaidPlan()
    {
        var owner = await _api.OwnerAsync();
        var connectionId = await CreateConnectionAsync(owner.AccessToken);

        var response = await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", owner.AccessToken, paidPlan: false);

        Assert.Equal("billing.upgrade_required", await TestApi.ErrorCodeAsync(response));
    }

    private async Task DrainJobsAsync()
    {
        var processor = factory.Services.GetRequiredService<IJobProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessNextAsync(TestContext.Current.CancellationToken); i++)
        {
        }
    }

    private async Task<Guid> CreateConnectionAsync(string accessToken)
    {
        var response = await Send(HttpMethod.Post, "/api/database/connections", accessToken, new
        {
            name = $"CRM {Guid.NewGuid():N}",
            provider = "PostgreSql",
            host = "203.0.113.10",
            port = 5432,
            database = "crm",
            username = "reporting",
            password = "s3cret-value",
            tlsMode = "VerifyFull",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("connection").GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string accessToken, object? body = null, bool paidPlan = true)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (paidPlan)
        {
            request.Headers.Add(TestEntitlements.Header, "unlimited");
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
