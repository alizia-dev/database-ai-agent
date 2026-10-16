using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DatabaseAIAgent.Domain.Authorization;

namespace DatabaseAIAgent.Api.Tests;

public sealed class TeamAndRoleTests(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    [Theory]
    [InlineData(SystemRoles.Viewer)]
    [InlineData(SystemRoles.Analyst)]
    public async Task MembersWithoutUsersView_CannotListTeam(string role)
    {
        var member = await _api.MemberAsync(role);

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/team/members", member.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Manager_CanViewTeamButCannotInvite()
    {
        var manager = await _api.MemberAsync(SystemRoles.Manager);

        var list = await _api.GetAsync("/api/team/members", manager.AccessToken);
        var invite = await _api.SendAsync(HttpMethod.Post, "/api/team/invitations", manager.AccessToken, new { email = TestApi.UniqueEmail("x"), role = SystemRoles.Viewer });

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
    }

    [Fact]
    public async Task Invitation_PreviewAcceptAndSingleUse()
    {
        var owner = await _api.OwnerAsync();
        var email = TestApi.UniqueEmail("invitee");
        await _api.SendAsync(HttpMethod.Post, "/api/team/invitations", owner.AccessToken, new { email, role = SystemRoles.Analyst });
        var token = factory.Emails.LatestLinkQuery(email, "accept-invite")["token"];

        var preview = await _api.PostAsync("/api/invitations/preview", new { token });
        var accept = await _api.PostAsync("/api/invitations/accept", new { token, displayName = "New Analyst", password = TestApi.Password });
        var again = await _api.PostAsync("/api/invitations/accept", new { token, displayName = "Again", password = TestApi.Password });

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains(email, await preview.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var session = await TestApi.ToSessionAsync(email, TestApi.Password, accept);
        Assert.Equal(SystemRoles.Analyst, session.User.Role);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Invitation_RevokedTokenCannotBeUsed()
    {
        var owner = await _api.OwnerAsync();
        var email = TestApi.UniqueEmail("revoked");
        var created = await _api.SendAsync(HttpMethod.Post, "/api/team/invitations", owner.AccessToken, new { email, role = SystemRoles.Viewer });
        var invitationId = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString();
        var token = factory.Emails.LatestLinkQuery(email, "accept-invite")["token"];

        var revoke = await _api.SendAsync(HttpMethod.Delete, $"/api/team/invitations/{invitationId}", owner.AccessToken);
        var accept = await _api.PostAsync("/api/invitations/accept", new { token, displayName = "Too Late", password = TestApi.Password });

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    [Fact]
    public async Task Invitation_CannotGrantOwnerOrInviteExistingMember()
    {
        var owner = await _api.OwnerAsync();

        var asOwner = await _api.SendAsync(HttpMethod.Post, "/api/team/invitations", owner.AccessToken, new { email = TestApi.UniqueEmail("o"), role = SystemRoles.Owner });
        var existing = await _api.SendAsync(HttpMethod.Post, "/api/team/invitations", owner.AccessToken, new { email = owner.Email, role = SystemRoles.Viewer });

        Assert.Equal(HttpStatusCode.BadRequest, asOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, existing.StatusCode);
    }

    [Fact]
    public async Task Admin_CannotManageOwnersOrThemselves()
    {
        var owner = await _api.OwnerAsync();
        var admin = await _api.MemberAsync(SystemRoles.Admin);

        var demoteOwner = await _api.SendAsync(HttpMethod.Put, $"/api/team/members/{owner.User.Id}/role", admin.AccessToken, new { role = SystemRoles.Viewer });
        var deactivateOwner = await _api.SendAsync(HttpMethod.Post, $"/api/team/members/{owner.User.Id}/deactivate", admin.AccessToken);
        var promoteSelf = await _api.SendAsync(HttpMethod.Put, $"/api/team/members/{admin.User.Id}/role", admin.AccessToken, new { role = SystemRoles.Owner });

        Assert.Equal("users.owner_protected", await TestApi.ErrorCodeAsync(demoteOwner));
        Assert.Equal(HttpStatusCode.Forbidden, deactivateOwner.StatusCode);
        Assert.Equal("users.cannot_manage_self", await TestApi.ErrorCodeAsync(promoteSelf));
    }

    [Fact]
    public async Task Admin_CannotAssignRoleAboveTheirOwn()
    {
        var admin = await _api.MemberAsync(SystemRoles.Admin);
        var viewer = await _api.MemberAsync(SystemRoles.Viewer);

        var response = await _api.SendAsync(HttpMethod.Put, $"/api/team/members/{viewer.User.Id}/role", admin.AccessToken, new { role = SystemRoles.Owner });

        Assert.Equal("users.role_not_allowed", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task ChangeRole_TakesEffectImmediately()
    {
        var owner = await _api.OwnerAsync();
        var member = await _api.MemberAsync(SystemRoles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/team/members", member.AccessToken)).StatusCode);

        var change = await _api.SendAsync(HttpMethod.Put, $"/api/team/members/{member.User.Id}/role", owner.AccessToken, new { role = SystemRoles.Manager });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/team/members", member.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task Deactivate_EndsSessionsImmediatelyAndBlocksSignIn()
    {
        var owner = await _api.OwnerAsync();
        var member = await _api.MemberAsync(SystemRoles.Analyst);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/account/me", member.AccessToken)).StatusCode);

        var deactivate = await _api.SendAsync(HttpMethod.Post, $"/api/team/members/{member.User.Id}/deactivate", owner.AccessToken);

        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/account/me", member.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.CookieRequestAsync("/api/auth/refresh", member.RefreshToken)).StatusCode);
        var login = await _api.PostAsync("/api/auth/login", new { email = member.Email, password = member.Password });
        Assert.Equal("auth.account_disabled", await TestApi.ErrorCodeAsync(login));

        await _api.SendAsync(HttpMethod.Post, $"/api/team/members/{member.User.Id}/reactivate", owner.AccessToken);
        await _api.LoginAsync(member.Email, member.Password);
    }

    [Fact]
    public async Task Roles_OwnerIsNotEditableAndAdminCannotEditAdmin()
    {
        var owner = await _api.OwnerAsync();
        var admin = await _api.MemberAsync(SystemRoles.Admin);

        var editOwner = await _api.SendAsync(HttpMethod.Put, "/api/roles/Owner/permissions", owner.AccessToken, new { permissions = Array.Empty<string>() });
        var adminEditsAdmin = await _api.SendAsync(HttpMethod.Put, "/api/roles/Admin/permissions", admin.AccessToken, new { permissions = new[] { Permissions.ChatUse } });
        var adminGrantsBillingManage = await _api.SendAsync(HttpMethod.Put, "/api/roles/Viewer/permissions", admin.AccessToken, new { permissions = new[] { Permissions.BillingManage } });
        var unknown = await _api.SendAsync(HttpMethod.Put, "/api/roles/Viewer/permissions", owner.AccessToken, new { permissions = new[] { "database.drop" } });

        Assert.Equal(HttpStatusCode.Forbidden, editOwner.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, adminEditsAdmin.StatusCode);
        Assert.Equal("roles.cannot_grant", await TestApi.ErrorCodeAsync(adminGrantsBillingManage));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Roles_PermissionChangesApplyToMembersImmediately()
    {
        var owner = await _api.OwnerAsync();
        var analyst = await _api.MemberAsync(SystemRoles.Analyst);
        var original = SystemRoles.DefaultPermissions[SystemRoles.Analyst];

        try
        {
            var update = await _api.SendAsync(HttpMethod.Put, "/api/roles/Analyst/permissions", owner.AccessToken, new { permissions = original.Append(Permissions.UsersView) });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/team/members", analyst.AccessToken)).StatusCode);
        }
        finally
        {
            await _api.SendAsync(HttpMethod.Put, "/api/roles/Analyst/permissions", owner.AccessToken, new { permissions = original });
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/team/members", analyst.AccessToken)).StatusCode);
    }
}
