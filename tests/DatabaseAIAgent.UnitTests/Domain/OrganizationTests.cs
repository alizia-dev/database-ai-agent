using DatabaseAIAgent.Domain.Common;
using DatabaseAIAgent.Domain.Organizations;

namespace DatabaseAIAgent.UnitTests.Domain;

public sealed class OrganizationTests
{
    [Fact]
    public void CreateDefault_UsesSingletonIdAndValidDefaults()
    {
        var organization = Organization.CreateDefault();

        Assert.Equal(Organization.SingletonId, organization.Id);
        Assert.Equal("My Workspace", organization.Name);
        Assert.Equal("UTC", organization.TimeZone);
        Assert.Equal("USD", organization.Currency);
        Assert.Equal(RetentionPolicy.Default, organization.Retention);
    }

    [Fact]
    public void Update_TrimsValuesNormalizesCurrencyAndBlanksOptionalFields()
    {
        var organization = Organization.CreateDefault();

        organization.Update("  Acme Ltd  ", "   ", " Ava ", "", "Europe/London", "gbp", "dd/MM/yyyy", ResponseStyle.Concise, RetentionPolicy.Default);

        Assert.Equal("Acme Ltd", organization.Name);
        Assert.Null(organization.Industry);
        Assert.Equal("Ava", organization.AssistantName);
        Assert.Null(organization.BusinessDescription);
        Assert.Equal("GBP", organization.Currency);
        Assert.Equal(ResponseStyle.Concise, organization.ResponseStyle);
    }

    [Theory]
    [InlineData("", "UTC", "USD", "yyyy-MM-dd")]
    [InlineData("Acme", "Mars/Olympus_Mons", "USD", "yyyy-MM-dd")]
    [InlineData("Acme", "UTC", "US", "yyyy-MM-dd")]
    [InlineData("Acme", "UTC", "U$D", "yyyy-MM-dd")]
    [InlineData("Acme", "UTC", "USD", "%")]
    public void Update_RejectsInvalidValues(string name, string timeZone, string currency, string dateFormat)
    {
        var organization = Organization.CreateDefault();

        Assert.Throws<DomainException>(() =>
            organization.Update(name, null, "Assistant", null, timeZone, currency, dateFormat, ResponseStyle.Balanced, RetentionPolicy.Default));
    }

    [Fact]
    public void Update_RejectsNameLongerThanMaximum()
    {
        var organization = Organization.CreateDefault();
        var name = new string('a', Organization.NameMaxLength + 1);

        Assert.Throws<DomainException>(() =>
            organization.Update(name, null, "Assistant", null, "UTC", "USD", "yyyy-MM-dd", ResponseStyle.Balanced, RetentionPolicy.Default));
    }

    [Fact]
    public void Update_RejectsUndefinedResponseStyle()
    {
        var organization = Organization.CreateDefault();

        Assert.Throws<DomainException>(() =>
            organization.Update("Acme", null, "Assistant", null, "UTC", "USD", "yyyy-MM-dd", (ResponseStyle)99, RetentionPolicy.Default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RetentionPolicy.MaximumDays + 1)]
    public void Update_RejectsOutOfRangeRetention(int days)
    {
        var organization = Organization.CreateDefault();
        var retention = RetentionPolicy.Default with { AuditLogDays = days };

        Assert.Throws<DomainException>(() =>
            organization.Update("Acme", null, "Assistant", null, "UTC", "USD", "yyyy-MM-dd", ResponseStyle.Balanced, retention));
    }

    [Fact]
    public void Update_AllowsIndefiniteRetention()
    {
        var organization = Organization.CreateDefault();
        var retention = new RetentionPolicy(null, null, null, null);

        organization.Update("Acme", null, "Assistant", null, "UTC", "USD", "yyyy-MM-dd", ResponseStyle.Balanced, retention);

        Assert.Equal(retention, organization.Retention);
    }

    [Fact]
    public void Update_WhenValidationFails_LeavesEntityUnchanged()
    {
        var organization = Organization.CreateDefault();

        Assert.Throws<DomainException>(() =>
            organization.Update("Renamed", null, "Assistant", null, "UTC", "invalid", "yyyy-MM-dd", ResponseStyle.Detailed, RetentionPolicy.Default));

        Assert.Equal("My Workspace", organization.Name);
        Assert.Equal(ResponseStyle.Balanced, organization.ResponseStyle);
    }
}
