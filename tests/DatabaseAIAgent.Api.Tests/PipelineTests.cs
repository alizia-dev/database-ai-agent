using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Api.Controllers;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Api.Middleware;

namespace DatabaseAIAgent.Api.Tests;

public sealed class PipelineTests(ApiFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task LiveHealth_ReturnsHealthyWithoutDependencies()
    {
        var response = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Responses_IncludeSecurityHeaders()
    {
        var response = await _client.GetAsync("/api/system/info", TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Contains("default-src 'none'", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task CorrelationId_ValidInboundValueIsEchoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "client-req-42");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("client-req-42", Header(response, CorrelationIdMiddleware.HeaderName));
    }

    [Fact]
    public async Task CorrelationId_UnsafeInboundValueIsReplaced()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "bad value {injected}");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var echoed = Header(response, CorrelationIdMiddleware.HeaderName);
        Assert.NotEqual("bad value {injected}", echoed);
        Assert.Matches("^[0-9a-f]{32}$", echoed);
    }

    [Fact]
    public async Task UnhandledException_ReturnsGenericProblemWithoutInternalDetails()
    {
        var owner = await _api.OwnerAsync();
        var response = await _api.GetAsync(ApiFactory.ThrowPath, owner.AccessToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SuperSecret123", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("customers", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", raw, StringComparison.Ordinal);

        var problem = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(GlobalExceptionHandler.GenericErrorMessage, problem.GetProperty("detail").GetString());
        Assert.Equal(Header(response, CorrelationIdMiddleware.HeaderName), problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Cors_AllowsConfiguredOriginOnly()
    {
        var allowed = await PreflightAsync(ApiFactory.AllowedOrigin);
        var denied = await PreflightAsync("https://evil.example.test");

        Assert.Equal(ApiFactory.AllowedOrigin, Header(allowed, "Access-Control-Allow-Origin"));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task SystemInfo_ReturnsPublicBuildInformationOnly()
    {
        var info = await _client.GetFromJsonAsync<SystemInfoResponse>("/api/system/info", TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("Database AI Agent", info.Name);
        Assert.DoesNotContain("+", info.Version, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRoute_IsUnauthorizedForAnonymousCallers_AndNotFoundWhenSignedIn()
    {
        var owner = await _api.OwnerAsync();

        var anonymous = await _client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);
        var signedIn = await _api.GetAsync("/api/does-not-exist", owner.AccessToken);

        // Authentication is required by default, so anonymous callers cannot probe which routes exist.
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/system/info");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : string.Empty;
}
