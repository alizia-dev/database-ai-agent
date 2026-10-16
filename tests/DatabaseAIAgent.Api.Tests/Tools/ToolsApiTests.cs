using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Api.Tests.Databases;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Domain.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Api.Tests.Tools;

public sealed class ToolsApiTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task Analysis_GeneratesToolDrafts_WhichNeedApprovalBeforeTheyRun()
    {
        var owner = await _api.OwnerAsync();
        var (connectionId, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "get_outstanding_invoices", "set_credit_limit");

        var reader = tools["get_outstanding_invoices"];
        var writer = tools["set_credit_limit"];
        Assert.Equal("PendingReview", reader.GetProperty("status").GetString());
        Assert.Equal(("Read", "Low"), (reader.GetProperty("operation").GetString(), reader.GetProperty("risk").GetString()));
        Assert.Equal(("Write", "High"), (writer.GetProperty("operation").GetString(), writer.GetProperty("risk").GetString()));

        var detail = await Json(await Send(HttpMethod.Get, $"/api/tools/{Id(reader)}", owner.AccessToken));
        var parameter = detail.GetProperty("parameters")[0];
        Assert.Equal(("minBalance", "p_min_balance", "Number"), (parameter.GetProperty("name").GetString(), parameter.GetProperty("sourceName").GetString(), parameter.GetProperty("type").GetString()));
        Assert.False(parameter.GetProperty("required").GetBoolean());
        var schema = detail.GetProperty("parametersSchema");
        Assert.Equal("number", schema.GetProperty("properties").GetProperty("minBalance").GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.True(detail.GetProperty("canApprove").GetBoolean());

        // Not runnable until approved.
        var name = Name(reader);
        var beforeApproval = await Json(await Execute(name, owner.AccessToken, new { minBalance = 100 }));
        Assert.Equal(("Denied", "tools.disabled"), (beforeApproval.GetProperty("outcome").GetString(), beforeApproval.GetProperty("reasonCode").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/tools/{Id(reader)}/approve", owner.AccessToken, new { acknowledgeDataChanges = false })).StatusCode);

        var analyst = await _api.MemberAsync(SystemRoles.Analyst);
        var result = await Json(await Execute(name, analyst.AccessToken, new { minBalance = "250.50" }));
        Assert.Equal("Succeeded", result.GetProperty("outcome").GetString());
        Assert.Equal(["id", "name"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(2, result.GetProperty("rows").GetArrayLength());

        // The pipeline passed the typed, source-named argument and asked for a read-only call with the tool's limits.
        var invocation = factory.Procedures.For("get_outstanding_invoices").Last(i => i.ConnectionId == connectionId);
        Assert.True(invocation.ReadOnly);
        Assert.Equal(("p_min_balance", 250.50m), (invocation.Arguments.Single().SourceName, (decimal)invocation.Arguments.Single().Value!));
        Assert.Equal((30, 500), (invocation.TimeoutSeconds, invocation.MaxRows));

        // Every attempt is audited, including the denial before approval; argument values are redacted.
        var executions = (await Json(await Send(HttpMethod.Get, $"/api/tools/{Id(reader)}", owner.AccessToken))).GetProperty("recentExecutions");
        Assert.Contains(executions.EnumerateArray(), e => e.GetProperty("status").GetString() == "Succeeded" && e.GetProperty("rowCount").GetInt32() == 2);
        Assert.Contains(executions.EnumerateArray(), e => e.GetProperty("reasonCode").GetString() == "tools.disabled");
    }

    [Fact]
    public async Task WriteTools_CannotBeApprovedWhileWriteToolsAreDisabled()
    {
        var owner = await _api.OwnerAsync();
        var (_, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "set_credit_limit");
        var writer = tools["set_credit_limit"];

        var detail = await Json(await Send(HttpMethod.Get, $"/api/tools/{Id(writer)}", owner.AccessToken));
        Assert.False(detail.GetProperty("canApprove").GetBoolean());
        Assert.True(detail.GetProperty("requiresConfirmation").GetBoolean());
        Assert.Equal(["Owner", "Admin"], detail.GetProperty("allowedRoles").EnumerateArray().Select(r => r.GetString()));

        var approve = await Send(HttpMethod.Post, $"/api/tools/{Id(writer)}/approve", owner.AccessToken, new { acknowledgeDataChanges = true });
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal("tools.write_disabled", await TestApi.ErrorCodeAsync(approve));
    }

    [Fact]
    public async Task Execution_ValidatesArgumentsAndEnforcesToolRoles()
    {
        var owner = await _api.OwnerAsync();
        var (_, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "get_top_customers");
        var tool = tools["get_top_customers"];
        await Send(HttpMethod.Post, $"/api/tools/{Id(tool)}/approve", owner.AccessToken, new { acknowledgeDataChanges = false });
        var name = Name(tool);
        var calls = factory.Procedures.Invocations.Count;

        var invalid = await Json(await Execute(name, owner.AccessToken, new { minBalance = "lots", dropTables = true }));
        Assert.Equal("InvalidArguments", invalid.GetProperty("outcome").GetString());
        var errors = invalid.GetProperty("argumentErrors");
        Assert.True(errors.TryGetProperty("minBalance", out _));
        Assert.Equal("Unknown argument.", errors.GetProperty("dropTables")[0].GetString());

        // Restrict the tool to Owner/Admin: an Analyst is now refused even though they hold tools.execute.
        var detail = await Json(await Send(HttpMethod.Get, $"/api/tools/{Id(tool)}", owner.AccessToken));
        var update = await Send(HttpMethod.Put, $"/api/tools/{Id(tool)}", owner.AccessToken, new
        {
            name,
            description = "Top customers by balance.",
            parameterDescriptions = new Dictionary<string, string> { ["minBalance"] = "Only customers above this balance." },
            timeoutSeconds = 10,
            maxRows = 50,
            maxResultKilobytes = 256,
            allowedRoles = new[] { "Owner", "Admin" },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await Json(update);
        Assert.Equal("Only customers above this balance.", updated.GetProperty("parameters")[0].GetProperty("description").GetString());
        Assert.Equal(detail.GetProperty("summary").GetProperty("id").GetGuid(), updated.GetProperty("summary").GetProperty("id").GetGuid());

        var analyst = await _api.MemberAsync(SystemRoles.Analyst);
        var refused = await Json(await Execute(name, analyst.AccessToken, new { }));
        Assert.Equal(("Denied", "tools.role_not_allowed"), (refused.GetProperty("outcome").GetString(), refused.GetProperty("reasonCode").GetString()));
        Assert.Equal(calls, factory.Procedures.Invocations.Count); // nothing reached the database

        var ok = await Json(await Execute(name, owner.AccessToken, new { }));
        Assert.Equal("Succeeded", ok.GetProperty("outcome").GetString());
        Assert.Equal((10, 50), (factory.Procedures.Invocations.Last().TimeoutSeconds, factory.Procedures.Invocations.Last().MaxRows));
    }

    [Fact]
    public async Task DatabaseFailures_AreReportedSafely()
    {
        var owner = await _api.OwnerAsync();
        var (_, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "get_slow_report");
        var tool = tools["get_slow_report"];
        await Send(HttpMethod.Post, $"/api/tools/{Id(tool)}/approve", owner.AccessToken, new { acknowledgeDataChanges = false });
        factory.Procedures.Failures["get_slow_report"] = ProcedureFailure.Timeout;

        var result = await Json(await Execute(Name(tool), owner.AccessToken, new { }));

        Assert.Equal(("TimedOut", "tools.timeout"), (result.GetProperty("outcome").GetString(), result.GetProperty("reasonCode").GetString()));
        Assert.Equal("The request took too long and was cancelled.", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ChangedProcedure_DisablesItsEnabledToolUntilReviewed()
    {
        var owner = await _api.OwnerAsync();
        var (connectionId, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "get_aging_report");
        var tool = tools["get_aging_report"];
        await Send(HttpMethod.Post, $"/api/tools/{Id(tool)}/approve", owner.AccessToken, new { acknowledgeDataChanges = false });

        // The procedure now writes data: re-analysis must disable the tool and reclassify it.
        var catalog = FakeDiscovery.Crm("get_aging_report");
        var changed = catalog.Routines[0] with { Definition = "BEGIN DELETE FROM invoices; END", DeclaredDataAccess = "VOLATILE" };
        factory.Discovery.Catalogs[connectionId] = catalog with { Routines = [changed] };
        await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", owner.AccessToken);
        await DrainJobsAsync();

        var detail = await Json(await Send(HttpMethod.Get, $"/api/tools/{Id(tool)}", owner.AccessToken));
        var summary = detail.GetProperty("summary");
        Assert.Equal(("Disabled", "ProcedureChanged"), (summary.GetProperty("status").GetString(), summary.GetProperty("disabledReason").GetString()));
        Assert.Equal(("Delete", "Critical"), (summary.GetProperty("operation").GetString(), summary.GetProperty("risk").GetString()));

        var result = await Json(await Execute(Name(tool), owner.AccessToken, new { }));
        Assert.Equal("tools.disabled", result.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task Permissions_AndPlan_AreEnforced()
    {
        var owner = await _api.OwnerAsync();
        var (connectionId, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "get_customer_list");
        var tool = tools["get_customer_list"];
        var viewer = await _api.MemberAsync(SystemRoles.Viewer);
        var manager = await _api.MemberAsync(SystemRoles.Manager);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/tools", viewer.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/tools", manager.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/tools/{Id(tool)}/approve", manager.AccessToken, new { acknowledgeDataChanges = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/tools/generate", manager.AccessToken)).StatusCode);

        // Without a paid plan, approval and execution are refused.
        var approve = await Send(HttpMethod.Post, $"/api/tools/{Id(tool)}/approve", owner.AccessToken, new { acknowledgeDataChanges = false }, paidPlan: false);
        Assert.Equal("billing.upgrade_required", await TestApi.ErrorCodeAsync(approve));
        var execute = await Send(HttpMethod.Post, $"/api/tools/{Name(tool)}/execute", owner.AccessToken, new { arguments = new { }, confirmed = false }, paidPlan: false);
        Assert.Equal("billing.upgrade_required", await TestApi.ErrorCodeAsync(execute));

        // Unauthenticated callers never reach the pipeline.
        var anonymous = await _api.Client.PostAsJsonAsync($"/api/tools/{Name(tool)}/execute", new { arguments = new { } }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task RejectReopenAndDisable_FollowTheLifecycle()
    {
        var owner = await _api.OwnerAsync();
        var (connectionId, tools) = await AnalyzedConnectionAsync(owner.AccessToken, "get_region_totals");
        var id = Id(tools["get_region_totals"]);

        var rejected = await Json(await Send(HttpMethod.Post, $"/api/tools/{id}/reject", owner.AccessToken, new { reason = "Not needed" }));
        Assert.Equal(("Rejected", "Not needed"), (rejected.GetProperty("summary").GetProperty("status").GetString(), rejected.GetProperty("rejectionReason").GetString()));
        Assert.Equal("tools.rejected", await TestApi.ErrorCodeAsync(await Send(HttpMethod.Post, $"/api/tools/{id}/approve", owner.AccessToken, new { acknowledgeDataChanges = false })));

        // Regeneration does not resurrect a rejected tool.
        var report = await Json(await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/tools/generate", owner.AccessToken));
        Assert.Equal(0, report.GetProperty("created").GetInt32());
        Assert.Equal("Rejected", (await Json(await Send(HttpMethod.Get, $"/api/tools/{id}", owner.AccessToken))).GetProperty("summary").GetProperty("status").GetString());

        Assert.Equal("PendingReview", (await Json(await Send(HttpMethod.Post, $"/api/tools/{id}/reopen", owner.AccessToken))).GetProperty("summary").GetProperty("status").GetString());
        Assert.Equal("Enabled", (await Json(await Send(HttpMethod.Post, $"/api/tools/{id}/approve", owner.AccessToken, new { acknowledgeDataChanges = false }))).GetProperty("summary").GetProperty("status").GetString());
        var disabled = (await Json(await Send(HttpMethod.Post, $"/api/tools/{id}/disable", owner.AccessToken))).GetProperty("summary");
        Assert.Equal(("Disabled", "ByAdministrator"), (disabled.GetProperty("status").GetString(), disabled.GetProperty("disabledReason").GetString()));

        // Verified classifications cannot be overridden.
        Assert.Equal("tools.classification_verified", await TestApi.ErrorCodeAsync(await Send(HttpMethod.Post, $"/api/tools/{id}/confirm-read-only", owner.AccessToken, new { confirmation = true })));
    }

    private async Task<(Guid ConnectionId, Dictionary<string, JsonElement> Tools)> AnalyzedConnectionAsync(string accessToken, params string[] procedures)
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
        var connectionId = (await Json(response)).GetProperty("connection").GetProperty("id").GetGuid();
        factory.Discovery.Catalogs[connectionId] = FakeDiscovery.Crm(procedures);

        Assert.Equal(HttpStatusCode.Accepted, (await Send(HttpMethod.Post, $"/api/database/connections/{connectionId}/analyze", accessToken)).StatusCode);
        await DrainJobsAsync();

        var list = await Json(await Send(HttpMethod.Get, $"/api/tools?connectionId={connectionId}", accessToken));
        var tools = list.EnumerateArray().ToDictionary(t => t.GetProperty("procedureName").GetString()!.Split('.')[1]);
        Assert.Equal(procedures.Length, tools.Count);
        return (connectionId, tools);
    }

    private static Guid Id(JsonElement tool) => tool.GetProperty("id").GetGuid();

    private static string Name(JsonElement tool) => tool.GetProperty("name").GetString()!;

    private Task<HttpResponseMessage> Execute(string name, string accessToken, object arguments, bool confirmed = false) =>
        Send(HttpMethod.Post, $"/api/tools/{name}/execute", accessToken, new { arguments, confirmed });

    private async Task DrainJobsAsync()
    {
        var processor = factory.Services.GetRequiredService<IJobProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessNextAsync(TestContext.Current.CancellationToken); i++)
        {
        }
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
