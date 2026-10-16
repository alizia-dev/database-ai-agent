using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Domain.Authorization;

namespace DatabaseAIAgent.Api.Tests;

public sealed class WorkspaceSettingsTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    private static object ValidSettings(string name, string currency = "EUR") => new
    {
        name,
        industry = "Manufacturing",
        assistantName = "Ava",
        businessDescription = "We build widgets.",
        timeZone = "Europe/Berlin",
        currency,
        dateFormat = "dd.MM.yyyy",
        responseStyle = 1,
        retention = new { conversationDays = 180, auditLogDays = 365, usageRecordDays = 365, knowledgeDocumentDays = (int?)null },
    };

    [Fact]
    public async Task AnyMember_CanReadSettings_ButOnlySettingsManagersCanUpdate()
    {
        var viewer = await _api.MemberAsync(SystemRoles.Viewer);

        var read = await _api.GetAsync("/api/workspace/settings", viewer.AccessToken);
        var write = await _api.SendAsync(HttpMethod.Put, "/api/workspace/settings", viewer.AccessToken, ValidSettings("Hijacked"));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Owner_UpdatesSettings_AndInvalidValuesAreRejected()
    {
        var owner = await _api.OwnerAsync();

        var updated = await _api.SendAsync(HttpMethod.Put, "/api/workspace/settings", owner.AccessToken, ValidSettings("Acme GmbH"));
        var invalid = await _api.SendAsync(HttpMethod.Put, "/api/workspace/settings", owner.AccessToken, ValidSettings("Acme GmbH", currency: "EURO"));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Acme GmbH", body.GetProperty("name").GetString());
        Assert.Equal("EUR", body.GetProperty("currency").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("workspace.invalid_settings", await TestApi.ErrorCodeAsync(invalid));
    }
}
