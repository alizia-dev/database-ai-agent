using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Common;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Security;
using DatabaseAIAgent.Security.Email;
using DatabaseAIAgent.Security.Tokens;
using DatabaseAIAgent.Security.Users;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.UnitTests.Security;

public sealed class AuthorizationModelTests
{
    [Fact]
    public void DefaultPermissions_CoverEveryRoleWithKnownPermissionsOnly()
    {
        Assert.Equal(SystemRoles.All.OrderBy(r => r), SystemRoles.DefaultPermissions.Keys.OrderBy(r => r));
        Assert.All(SystemRoles.DefaultPermissions.Values.SelectMany(p => p), p => Assert.True(Permissions.IsKnown(p)));
    }

    [Fact]
    public void Owner_HasEverything_AndAdminCannotManageBilling()
    {
        Assert.Equal(Permissions.All, SystemRoles.DefaultPermissions[SystemRoles.Owner]);
        Assert.DoesNotContain(Permissions.BillingManage, SystemRoles.DefaultPermissions[SystemRoles.Admin]);
    }

    [Fact]
    public void LowerRoles_HaveNoManagementPermissions()
    {
        string[] managementPermissions = [Permissions.UsersManage, Permissions.BillingManage, Permissions.DatabaseManage, Permissions.ToolsManage, Permissions.SettingsManage, Permissions.AuditView];

        foreach (var role in new[] { SystemRoles.Analyst, SystemRoles.Viewer })
        {
            Assert.Empty(SystemRoles.DefaultPermissions[role].Intersect(managementPermissions));
        }
    }

    [Theory]
    [InlineData(SystemRoles.Owner, SystemRoles.Owner, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.Admin, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.Viewer, true)]
    [InlineData(SystemRoles.Admin, SystemRoles.Owner, false)]
    [InlineData(SystemRoles.Manager, SystemRoles.Admin, false)]
    [InlineData(SystemRoles.Viewer, SystemRoles.Analyst, false)]
    public void CanAssign_OnlyAllowsRolesAtOrBelowTheActor(string actor, string target, bool expected) =>
        Assert.Equal(expected, UserDirectory.CanAssign(actor, target));

    [Fact]
    public void RolePermission_RejectsUnknownPermission() =>
        Assert.Throws<DomainException>(() => new RolePermission(Guid.NewGuid(), "database.drop"));

    [Fact]
    public void Invitation_CannotGrantOwner() =>
        Assert.Throws<DomainException>(() => new UserInvitation("a@b.test", SystemRoles.Owner, new string('a', 64), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));

    [Fact]
    public void Invitation_IsSingleUse()
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = new UserInvitation("a@b.test", SystemRoles.Viewer, new string('a', 64), Guid.NewGuid(), now, now.AddDays(1));

        invitation.Accept(now);

        Assert.False(invitation.IsPending(now));
        Assert.Throws<DomainException>(() => invitation.Accept(now));
    }

    [Fact]
    public void OpaqueToken_IsHighEntropyAndHashIsDeterministic()
    {
        var (token1, hash1) = OpaqueToken.Create();
        var (token2, _) = OpaqueToken.Create();

        Assert.NotEqual(token1, token2);
        Assert.True(OpaqueToken.LooksValid(token1));
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, OpaqueToken.Hash(token1));
    }

    [Fact]
    public void AccountEmails_EncodeUserControlledValuesInHtml()
    {
        var emails = new AccountEmails(Options.Create(new AppUrlOptions { PublicUrl = "https://app.example.test" }));

        var message = emails.Invitation("x@example.test", "<script>alert(1)</script>", "Eve \"><img src=x>", "Viewer", "token123");

        Assert.DoesNotContain("<script>", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("https://app.example.test/accept-invite?token=token123", message.TextBody, StringComparison.Ordinal);
    }
}
