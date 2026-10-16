using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Tools;

/// <summary>What a tool does to the customer database.</summary>
public enum ToolOperation
{
    Read = 0,
    Write = 1,
    Delete = 2,

    /// <summary>Changes schema or permissions. Never enabled for AI use.</summary>
    Admin = 3,
}

public enum ToolRisk
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

/// <summary>How the operation type was determined.</summary>
public enum ClassificationSource
{
    /// <summary>Static analysis of the routine definition.</summary>
    DefinitionAnalysis = 0,

    /// <summary>A declaration the engine enforces (PostgreSQL STABLE/IMMUTABLE) or declares (MySQL data access).</summary>
    Declaration = 1,

    /// <summary>The definition was not readable; classified conservatively from the name.</summary>
    Unverified = 2,

    /// <summary>An administrator confirmed the classification of an unverified routine.</summary>
    AdministratorConfirmed = 3,
}

public enum ToolStatus
{
    /// <summary>Generated, not yet reviewed. Not available to the AI.</summary>
    PendingReview = 0,
    Enabled = 1,
    Disabled = 2,
    Rejected = 3,
}

public enum ToolDisabledReason
{
    None = 0,
    ByAdministrator = 1,

    /// <summary>The procedure's definition or parameters changed after approval; it must be reviewed again.</summary>
    ProcedureChanged = 2,

    /// <summary>The procedure no longer exists or can no longer be executed by the connection's account.</summary>
    ProcedureUnavailable = 3,
}

/// <summary>
/// An AI tool generated from an approved stored procedure: the only way the assistant can touch customer data.
/// Tools are never available until an administrator approves them; any change to the underlying procedure disables
/// the tool until it is reviewed again.
/// </summary>
public sealed class AiTool : AuditableEntity
{
    public const int NameMaxLength = 64;
    public const int DescriptionMaxLength = 1000;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 300;
    public const int MaxRowsLimit = 10_000;
    public const int MaxResultBytesLimit = 10 * 1024 * 1024;

    private AiTool()
    {
    }

    public AiTool(Guid connectionId, Guid storedProcedureId, string name, string description, ToolClassification classification, string? definitionHash, DateTimeOffset now)
    {
        ConnectionId = connectionId;
        StoredProcedureId = storedProcedureId;
        Rename(name);
        Describe(description);
        Classify(classification);
        SourceDefinitionHash = definitionHash;
        Status = ToolStatus.PendingReview;
        TimeoutSeconds = 30;
        MaxRows = 500;
        MaxResultBytes = 1024 * 1024;
        AllowedRoles = DefaultRoles(classification.Operation).Select(r => new ToolPermission(r)).ToList();
        GeneratedAt = now;
    }

    public Guid ConnectionId { get; private set; }

    public Guid StoredProcedureId { get; private set; }

    /// <summary>Function name exposed to the model (snake_case, unique in the workspace).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Explanation for the model of when and how to use the tool. Editable by administrators.</summary>
    public string Description { get; private set; } = string.Empty;

    public ToolOperation Operation { get; private set; }

    public ToolRisk Risk { get; private set; }

    public ClassificationSource ClassificationSource { get; private set; }

    public List<string> ClassificationReasons { get; private set; } = [];

    public ToolStatus Status { get; private set; }

    public ToolDisabledReason DisabledReason { get; private set; }

    /// <summary>Set when a parameter type cannot be mapped safely; such tools cannot be approved.</summary>
    public string? UnsupportedReason { get; private set; }

    public List<ToolParameter> Parameters { get; private set; } = [];

    /// <summary>Roles whose members may run the tool (in addition to the <c>tools.execute</c> permission).</summary>
    public List<ToolPermission> AllowedRoles { get; private set; } = [];

    public int TimeoutSeconds { get; private set; }

    public int MaxRows { get; private set; }

    public int MaxResultBytes { get; private set; }

    /// <summary>Hash of the procedure definition this tool was generated from / approved against.</summary>
    public string? SourceDefinitionHash { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public string? RejectionReason { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public bool IsAvailable => Status == ToolStatus.Enabled;

    /// <summary>Writes and deletes always need the user's explicit confirmation before each execution.</summary>
    public bool RequiresConfirmation => Operation != ToolOperation.Read;

    public void Rename(string name)
    {
        var value = Text.Required(name, nameof(Name), NameMaxLength);
        if (!IsValidName(value))
        {
            throw new DomainException("Tool names must start with a letter and contain only lowercase letters, digits and underscores (max 64).");
        }

        Name = value;
    }

    public void Describe(string description) => Description = Text.Required(description, nameof(Description), DescriptionMaxLength);

    public void SetParameters(IEnumerable<ToolParameter> parameters, string? unsupportedReason)
    {
        Parameters = parameters.OrderBy(p => p.Ordinal).ToList();
        UnsupportedReason = unsupportedReason;
    }

    public void SetLimits(int timeoutSeconds, int maxRows, int maxResultBytes)
    {
        if (timeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds || maxRows is < 1 or > MaxRowsLimit || maxResultBytes is < 1024 or > MaxResultBytesLimit)
        {
            throw new DomainException($"Timeout must be {MinTimeoutSeconds}-{MaxTimeoutSeconds}s, max rows 1-{MaxRowsLimit}, max result size 1 KB-10 MB.");
        }

        (TimeoutSeconds, MaxRows, MaxResultBytes) = (timeoutSeconds, maxRows, maxResultBytes);
    }

    public void SetAllowedRoles(IEnumerable<string> roles)
    {
        var list = roles.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0 || list.Any(r => !SystemRoles.IsKnown(r)))
        {
            throw new DomainException("Choose at least one valid role.");
        }

        AllowedRoles = list.Select(r => new ToolPermission(r)).ToList();
    }

    public bool AllowsRole(string role) => AllowedRoles.Any(r => r.Role == role);

    public void Classify(ToolClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        Operation = classification.Operation;
        Risk = classification.Risk;
        ClassificationSource = classification.Source;
        ClassificationReasons = classification.Reasons.Take(10).ToList();
    }

    public void Approve(Guid userId, DateTimeOffset at)
    {
        if (Status is ToolStatus.Rejected)
        {
            throw new DomainException("Reopen a rejected tool before approving it.");
        }

        if (UnsupportedReason is not null)
        {
            throw new DomainException(UnsupportedReason);
        }

        if (Operation == ToolOperation.Admin)
        {
            throw new DomainException("Tools that change the database schema or permissions cannot be enabled.");
        }

        Status = ToolStatus.Enabled;
        DisabledReason = ToolDisabledReason.None;
        ApprovedByUserId = userId;
        ApprovedAt = at;
    }

    /// <summary>Takes the tool out of service. Rejected tools stay rejected.</summary>
    public void Disable(ToolDisabledReason reason)
    {
        if (Status == ToolStatus.Rejected)
        {
            return;
        }

        Status = ToolStatus.Disabled;
        DisabledReason = reason;
    }

    /// <summary>The procedure is available again after being unavailable: it needs a fresh review.</summary>
    public void SourceRestored()
    {
        if (Status == ToolStatus.Disabled && DisabledReason == ToolDisabledReason.ProcedureUnavailable)
        {
            Status = ToolStatus.PendingReview;
            DisabledReason = ToolDisabledReason.None;
        }
    }

    public void Reject(string? reason)
    {
        Status = ToolStatus.Rejected;
        RejectionReason = Text.Optional(reason, nameof(RejectionReason), 500);
        DisabledReason = ToolDisabledReason.None;
    }

    public void Reopen()
    {
        if (Status == ToolStatus.Rejected)
        {
            Status = ToolStatus.PendingReview;
            RejectionReason = null;
        }
    }

    /// <summary>Records that the source procedure changed: the tool needs a fresh review before it can run again.</summary>
    public void SourceChanged(string? newDefinitionHash)
    {
        SourceDefinitionHash = newDefinitionHash;
        if (Status == ToolStatus.Enabled)
        {
            Status = ToolStatus.Disabled;
            DisabledReason = ToolDisabledReason.ProcedureChanged;
        }
    }

    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= NameMaxLength
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    /// <summary>Read tools default to every role; tools that change data default to Owner and Admin only.</summary>
    public static IReadOnlyList<string> DefaultRoles(ToolOperation operation) =>
        operation == ToolOperation.Read ? SystemRoles.All : [SystemRoles.Owner, SystemRoles.Admin];
}

public sealed record ToolClassification(ToolOperation Operation, ToolRisk Risk, ClassificationSource Source, IReadOnlyList<string> Reasons);

/// <summary>Role allowed to run a tool.</summary>
public sealed class ToolPermission
{
    private ToolPermission()
    {
    }

    public ToolPermission(string role)
    {
        Role = role;
    }

    public string Role { get; private set; } = string.Empty;
}

#pragma warning disable CA1720 // Members deliberately mirror JSON Schema type names.
public enum ToolParameterType
{
    String = 0,
    Integer = 1,
    Number = 2,
    Boolean = 3,
    Date = 4,
    DateTime = 5,
    Time = 6,
    Uuid = 7,
}
#pragma warning restore CA1720

/// <summary>A tool input, mapped from a procedure parameter. Values are always passed as typed DB parameters.</summary>
public sealed class ToolParameter
{
    public const int DescriptionMaxLength = 500;

    private ToolParameter()
    {
    }

    public ToolParameter(string name, string sourceName, int ordinal, ToolParameterType type, string sourceDataType, bool required, int? maxLength, decimal? minimum, decimal? maximum, string? description)
    {
        Name = name;
        SourceName = sourceName;
        Ordinal = ordinal;
        Type = type;
        SourceDataType = sourceDataType;
        Required = required;
        MaxLength = maxLength;
        Minimum = minimum;
        Maximum = maximum;
        Describe(description);
    }

    /// <summary>Name the model uses (camelCase).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The procedure's own parameter name.</summary>
    public string SourceName { get; private set; } = string.Empty;

    public int Ordinal { get; private set; }

    public ToolParameterType Type { get; private set; }

    public string SourceDataType { get; private set; } = string.Empty;

    public bool Required { get; private set; }

    public int? MaxLength { get; private set; }

    public decimal? Minimum { get; private set; }

    public decimal? Maximum { get; private set; }

    public string? Description { get; private set; }

    public void Describe(string? description) => Description = Text.Optional(description, nameof(Description), DescriptionMaxLength);
}
