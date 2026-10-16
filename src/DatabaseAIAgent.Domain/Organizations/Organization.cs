using System.Globalization;
using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Organizations;

/// <summary>
/// The workspace-level settings for this deployment. The platform is single-tenant: exactly one
/// Organization row exists, identified by <see cref="SingletonId"/>. Customer separation is achieved by
/// separate deployments, never by shared-schema isolation.
/// </summary>
public sealed class Organization : AuditableEntity
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-000000000001");

    public const int NameMaxLength = 200;
    public const int IndustryMaxLength = 100;
    public const int AssistantNameMaxLength = 60;
    public const int BusinessDescriptionMaxLength = 4000;
    public const int TimeZoneMaxLength = 64;
    public const int DateFormatMaxLength = 32;

    private Organization()
        : base(SingletonId)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Industry { get; private set; }

    public string AssistantName { get; private set; } = string.Empty;

    public string? BusinessDescription { get; private set; }

    /// <summary>IANA time zone id, e.g. "America/New_York" or "UTC".</summary>
    public string TimeZone { get; private set; } = string.Empty;

    /// <summary>ISO 4217 currency code, e.g. "USD".</summary>
    public string Currency { get; private set; } = string.Empty;

    /// <summary>.NET-compatible date format pattern used when presenting dates, e.g. "yyyy-MM-dd".</summary>
    public string DateFormat { get; private set; } = string.Empty;

    public ResponseStyle ResponseStyle { get; private set; }

    public RetentionPolicy Retention { get; private set; } = RetentionPolicy.Default;

    public static Organization CreateDefault() => Create(
        name: "My Workspace",
        industry: null,
        assistantName: "Data Assistant",
        businessDescription: null,
        timeZone: "UTC",
        currency: "USD",
        dateFormat: "yyyy-MM-dd",
        responseStyle: ResponseStyle.Balanced,
        retention: RetentionPolicy.Default);

    public static Organization Create(
        string name,
        string? industry,
        string assistantName,
        string? businessDescription,
        string timeZone,
        string currency,
        string dateFormat,
        ResponseStyle responseStyle,
        RetentionPolicy retention)
    {
        var organization = new Organization();
        organization.Update(name, industry, assistantName, businessDescription, timeZone, currency, dateFormat, responseStyle, retention);
        return organization;
    }

    public void Update(
        string name,
        string? industry,
        string assistantName,
        string? businessDescription,
        string timeZone,
        string currency,
        string dateFormat,
        ResponseStyle responseStyle,
        RetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(retention);

        var validName = Required(name, nameof(Name), NameMaxLength);
        var validIndustry = Optional(industry, nameof(Industry), IndustryMaxLength);
        var validAssistantName = Required(assistantName, nameof(AssistantName), AssistantNameMaxLength);
        var validDescription = Optional(businessDescription, nameof(BusinessDescription), BusinessDescriptionMaxLength);
        var validTimeZone = ValidTimeZone(timeZone);
        var validCurrency = ValidCurrency(currency);
        var validDateFormat = ValidDateFormat(dateFormat);

        if (!Enum.IsDefined(responseStyle))
        {
            throw new DomainException("Response style is not valid.");
        }

        retention.Validate();

        // Assign only after every value has been validated so a failed update leaves the entity unchanged.
        Name = validName;
        Industry = validIndustry;
        AssistantName = validAssistantName;
        BusinessDescription = validDescription;
        TimeZone = validTimeZone;
        Currency = validCurrency;
        DateFormat = validDateFormat;
        ResponseStyle = responseStyle;
        Retention = retention;
    }

    private static string Required(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException($"{name} is required.");
        }

        return trimmed.Length > maxLength
            ? throw new DomainException($"{name} must be at most {maxLength} characters.")
            : trimmed;
    }

    private static string? Optional(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > maxLength
            ? throw new DomainException($"{name} must be at most {maxLength} characters.")
            : trimmed;
    }

    private static string ValidTimeZone(string? timeZone)
    {
        var value = Required(timeZone, nameof(TimeZone), TimeZoneMaxLength);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(value, out _))
        {
            throw new DomainException("Time zone is not recognized. Use an IANA id such as 'America/New_York'.");
        }

        return value;
    }

    private static string ValidCurrency(string? currency)
    {
        var value = Required(currency, nameof(Currency), 3).ToUpperInvariant();
        if (value.Length != 3 || !value.All(char.IsAsciiLetterUpper))
        {
            throw new DomainException("Currency must be a 3-letter ISO 4217 code such as 'USD'.");
        }

        return value;
    }

    private static string ValidDateFormat(string? dateFormat)
    {
        var value = Required(dateFormat, nameof(DateFormat), DateFormatMaxLength);
        try
        {
            _ = new DateTime(2000, 1, 31, 0, 0, 0, DateTimeKind.Unspecified).ToString(value, CultureInfo.InvariantCulture);
        }
        catch (FormatException ex)
        {
            throw new DomainException("Date format is not a valid pattern.", ex);
        }

        return value;
    }
}
