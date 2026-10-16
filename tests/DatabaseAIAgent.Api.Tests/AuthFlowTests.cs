using System.Net;
using DatabaseAIAgent.Domain.Authorization;

namespace DatabaseAIAgent.Api.Tests;

public sealed class AuthFlowTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task FirstRegisteredUser_IsOwnerWithEveryPermission()
    {
        var owner = await _api.OwnerAsync();

        var me = await _api.GetAsync("/api/account/me", owner.AccessToken);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(SystemRoles.Owner, owner.User.Role);
        Assert.Equal(Permissions.All.OrderBy(p => p), owner.User.Permissions.OrderBy(p => p));
    }

    [Fact]
    public async Task Registration_IsInvitationOnlyOnceAnOwnerExists()
    {
        await _api.OwnerAsync();

        var response = await _api.PostAsync("/api/auth/register", new { email = TestApi.UniqueEmail("late"), password = TestApi.Password, displayName = "Late" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.registration_closed", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Register_InvalidInput_ReturnsFieldErrors()
    {
        var response = await _api.PostAsync("/api/auth/register", new { email = "not-an-email", password = "", displayName = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"email\"", body, StringComparison.Ordinal);
        Assert.Contains("\"password\"", body, StringComparison.Ordinal);
        Assert.Contains("\"displayName\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_UnknownEmailAndWrongPassword_ReturnTheSameError()
    {
        var owner = await _api.OwnerAsync();

        var unknown = await _api.PostAsync("/api/auth/login", new { email = TestApi.UniqueEmail("nobody"), password = TestApi.Password });
        var wrong = await _api.PostAsync("/api/auth/login", new { email = owner.Email, password = "not the password at all" });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("auth.invalid_credentials", await TestApi.ErrorCodeAsync(unknown));
        Assert.Equal("auth.invalid_credentials", await TestApi.ErrorCodeAsync(wrong));
    }

    [Fact]
    public async Task Login_UnverifiedEmail_IsRejectedUntilVerified()
    {
        var email = await _api.CreateUserDirectlyAsync(emailConfirmed: false);

        var response = await _api.PostAsync("/api/auth/login", new { email, password = TestApi.Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.email_not_verified", await TestApi.ErrorCodeAsync(response));

        Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync("/api/auth/resend-verification", new { email })).StatusCode);
        await _api.VerifyEmailFromInboxAsync(email);
        await _api.LoginAsync(email, TestApi.Password);
    }

    [Fact]
    public async Task VerifyEmail_WithTamperedToken_IsRejected()
    {
        var email = await _api.CreateUserDirectlyAsync(emailConfirmed: false);
        await _api.PostAsync("/api/auth/resend-verification", new { email });
        var link = factory.Emails.LatestLinkQuery(email, "verify-email");

        var response = await _api.PostAsync("/api/auth/verify-email", new { userId = link["userId"], token = link["token"] + "AA" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.invalid_link", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Login_LocksAccountAfterFiveFailures()
    {
        var email = await _api.CreateUserDirectlyAsync(emailConfirmed: true);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/auth/login", new { email, password = "wrong password attempt" })).StatusCode);
        }

        var fifth = await _api.PostAsync("/api/auth/login", new { email, password = "wrong password attempt" });
        var correctWhileLocked = await _api.PostAsync("/api/auth/login", new { email, password = TestApi.Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, correctWhileLocked.StatusCode);
        Assert.Equal("auth.locked_out", await TestApi.ErrorCodeAsync(correctWhileLocked));
    }

    [Fact]
    public async Task Login_SetsHardenedRefreshCookieAndKeepsTokenOutOfBody()
    {
        var owner = await _api.OwnerAsync();
        var response = await _api.PostAsync("/api/auth/login", new { email = owner.Email, password = owner.Password });

        var cookie = TestApi.RefreshCookieHeader(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestApi.ReadRefreshCookie(response)!, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_RotatesTheTokenAndRejectsImmediateReplayAsConflict()
    {
        var owner = await _api.OwnerAsync();

        var refreshed = await _api.CookieRequestAsync("/api/auth/refresh", owner.RefreshToken);
        var replay = await _api.CookieRequestAsync("/api/auth/refresh", owner.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var successor = TestApi.ReadRefreshCookie(refreshed);
        Assert.NotNull(successor);
        Assert.NotEqual(owner.RefreshToken, successor);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.CookieRequestAsync("/api/auth/refresh", successor)).StatusCode);
    }

    [Fact]
    public async Task Refresh_RequiresCsrfHeaderAndValidCookie()
    {
        var owner = await _api.OwnerAsync();

        var noHeader = await _api.CookieRequestAsync("/api/auth/refresh", owner.RefreshToken, includeCsrfHeader: false);
        var noCookie = await _api.CookieRequestAsync("/api/auth/refresh", refreshToken: null);
        var garbage = await _api.CookieRequestAsync("/api/auth/refresh", "not-a-real-token");

        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken()
    {
        var owner = await _api.OwnerAsync();

        var logout = await _api.CookieRequestAsync("/api/auth/logout", owner.RefreshToken);
        var refresh = await _api.CookieRequestAsync("/api/auth/refresh", owner.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_LooksIdenticalAndSendsNothing()
    {
        var email = TestApi.UniqueEmail("ghost");

        var response = await _api.PostAsync("/api/auth/forgot-password", new { email });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(factory.Emails.To(email));
    }

    [Fact]
    public async Task ResetPassword_ChangesPasswordEndsSessionsAndIsSingleUse()
    {
        var member = await _api.MemberAsync(SystemRoles.Analyst);
        const string newPassword = "a brand new long passphrase";

        await _api.PostAsync("/api/auth/forgot-password", new { email = member.Email });
        var link = factory.Emails.LatestLinkQuery(member.Email, "reset-password");
        var reset = await _api.PostAsync("/api/auth/reset-password", new { email = member.Email, token = link["token"], newPassword });
        var reused = await _api.PostAsync("/api/auth/reset-password", new { email = member.Email, token = link["token"], newPassword = "yet another long passphrase" });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/auth/login", new { email = member.Email, password = TestApi.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CookieRequestAsync("/api/auth/refresh", member.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/account/me", member.AccessToken)).StatusCode);
        await _api.LoginAsync(member.Email, newPassword);
    }

    [Fact]
    public async Task ResetPassword_RejectsWeakPassword()
    {
        var member = await _api.MemberAsync(SystemRoles.Viewer);
        await _api.PostAsync("/api/auth/forgot-password", new { email = member.Email });
        var link = factory.Emails.LatestLinkQuery(member.Email, "reset-password");

        var response = await _api.PostAsync("/api/auth/reset-password", new { email = member.Email, token = link["token"], newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.password_invalid", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ChangePassword_InvalidatesOldAccessTokenAndReturnsNewSession()
    {
        var member = await _api.MemberAsync(SystemRoles.Viewer);

        var wrongCurrent = await _api.SendAsync(HttpMethod.Post, "/api/account/change-password", member.AccessToken,
            new { currentPassword = "not my current password", newPassword = "another long passphrase" });
        var changed = await _api.SendAsync(HttpMethod.Post, "/api/account/change-password", member.AccessToken,
            new { currentPassword = TestApi.Password, newPassword = "another long passphrase" });

        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var session = await TestApi.ToSessionAsync(member.Email, "another long passphrase", changed);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/account/me", member.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CookieRequestAsync("/api/auth/refresh", member.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/account/me", session.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task LogoutEverywhere_InvalidatesAllSessions()
    {
        var member = await _api.MemberAsync(SystemRoles.Viewer);
        var secondDevice = await _api.LoginAsync(member.Email, member.Password);

        var response = await _api.SendAsync(HttpMethod.Post, "/api/account/logout-everywhere", member.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/account/me", secondDevice.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CookieRequestAsync("/api/auth/refresh", secondDevice.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoints_RequireAuthentication()
    {
        foreach (var path in new[] { "/api/account/me", "/api/team/members", "/api/roles", "/api/workspace/settings" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(path, accessToken: null)).StatusCode);
        }
    }

    [Fact]
    public async Task TamperedAccessToken_IsRejected()
    {
        var owner = await _api.OwnerAsync();
        var parts = owner.AccessToken.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{new string('A', parts[2].Length)}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/account/me", tampered)).StatusCode);
    }

    [Fact]
    public async Task UpdateProfile_ChangesDisplayName()
    {
        var member = await _api.MemberAsync(SystemRoles.Viewer);

        var response = await _api.SendAsync(HttpMethod.Put, "/api/account/profile", member.AccessToken, new { displayName = "  Renamed Person  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"displayName\":\"Renamed Person\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
