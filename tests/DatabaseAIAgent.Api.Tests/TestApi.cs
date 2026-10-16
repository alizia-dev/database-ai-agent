using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Security.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DatabaseAIAgent.Api.Tests;

public sealed record SessionBody(string AccessToken, DateTimeOffset AccessTokenExpiresAt, CurrentUserDto User);

/// <summary>A signed-in test user: its access token plus the raw refresh-token cookie value.</summary>
public sealed record TestSession(string Email, string Password, string AccessToken, string RefreshToken, CurrentUserDto User);

/// <summary>Drives the real HTTP API. Cookies are handled explicitly because the refresh cookie is Secure-only.</summary>
public sealed class TestApi(ApiFactory factory)
{
    public const string Password = "correct horse battery staple";
    public const string RefreshCookieName = "__Secure-daa_rt";

    private static readonly SemaphoreSlim OwnerGate = new(1, 1);
    private static TestSession? _owner;

    public HttpClient Client { get; } = factory.CreateClient();

    public static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.test";

    /// <summary>
    /// The workspace Owner: the first account ever registered in the shared test host (registration closes afterwards).
    /// </summary>
    public async Task<TestSession> OwnerAsync()
    {
        await OwnerGate.WaitAsync();
        try
        {
            if (_owner is null)
            {
                var email = UniqueEmail("owner");
                var register = await PostAsync("/api/auth/register", new { email, password = Password, displayName = "Olivia Owner" });
                Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
                await VerifyEmailFromInboxAsync(email);
                _owner = await LoginAsync(email, Password);
            }

            // Return a fresh session each time so tests do not share refresh-token state.
            return await LoginAsync(_owner.Email, _owner.Password);
        }
        finally
        {
            OwnerGate.Release();
        }
    }

    /// <summary>Creates a member with the given role through the real invitation flow and returns their session.</summary>
    public async Task<TestSession> MemberAsync(string role, string? email = null)
    {
        var owner = await OwnerAsync();
        email ??= UniqueEmail(role.ToLowerInvariant());
        var invite = await SendAsync(HttpMethod.Post, "/api/team/invitations", owner.AccessToken, new { email, role });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);

        var token = factory.Emails.LatestLinkQuery(email, "accept-invite")["token"];
        var accepted = await PostAsync("/api/invitations/accept", new { token, displayName = $"{role} Person", password = Password });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return await ToSessionAsync(email, Password, accepted);
    }

    /// <summary>Creates an account directly (bypassing registration), e.g. to test unverified sign-in.</summary>
    public async Task<string> CreateUserDirectlyAsync(bool emailConfirmed, string role = SystemRoles.Viewer)
    {
        var email = UniqueEmail("direct");
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { Email = email, UserName = email, DisplayName = "Direct User", EmailConfirmed = emailConfirmed, CreatedAt = DateTimeOffset.UtcNow };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return email;
    }

    public async Task VerifyEmailFromInboxAsync(string email)
    {
        var link = factory.Emails.LatestLinkQuery(email, "verify-email");
        var verify = await PostAsync("/api/auth/verify-email", new { userId = link["userId"], token = link["token"] });
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);
    }

    public async Task<TestSession> LoginAsync(string email, string password)
    {
        var response = await PostAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ToSessionAsync(email, password, response);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object body) =>
        Client.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> GetAsync(string path, string? accessToken) =>
        SendAsync(HttpMethod.Get, path, accessToken);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Calls a cookie-authenticated endpoint (refresh/logout) the way the browser client does.</summary>
    public async Task<HttpResponseMessage> CookieRequestAsync(string path, string? refreshToken, bool includeCsrfHeader = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (refreshToken is not null)
        {
            request.Headers.Add("Cookie", $"{RefreshCookieName}={refreshToken}");
        }

        if (includeCsrfHeader)
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<TestSession> ToSessionAsync(string email, string password, HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<SessionBody>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return new TestSession(email, password, body.AccessToken, ReadRefreshCookie(response) ?? throw new InvalidOperationException("No refresh cookie set."), body.User);
    }

    public static string? ReadRefreshCookie(HttpResponseMessage response) =>
        RefreshCookieHeader(response)?.Split(';')[0].Split('=', 2)[1] is { Length: > 0 } value ? value : null;

    public static string? RefreshCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(RefreshCookieName + "=", StringComparison.Ordinal))
            : null;

    public static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
