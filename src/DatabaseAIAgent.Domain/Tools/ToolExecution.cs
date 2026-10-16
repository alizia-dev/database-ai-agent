using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Tools;

public enum ToolExecutionStatus
{
    Succeeded = 0,

    /// <summary>Stopped before reaching the database (permission, plan, validation, disabled tool).</summary>
    Denied = 1,
    Failed = 2,
    TimedOut = 3,

    /// <summary>A write/delete tool awaiting the user's confirmation; nothing was executed.</summary>
    ConfirmationRequired = 4,
}

/// <summary>
/// One attempt to run a tool, recorded whether it succeeded or was denied. Parameter values are redacted (text values
/// are replaced by their length) and result rows are never stored.
/// </summary>
public sealed class ToolExecution : Entity
{
    private ToolExecution()
    {
    }

    public ToolExecution(Guid toolId, Guid userId, Guid? conversationId, string redactedArguments, DateTimeOffset startedAt)
    {
        ToolId = toolId;
        UserId = userId;
        ConversationId = conversationId;
        RedactedArguments = redactedArguments.Length <= 4000 ? redactedArguments : redactedArguments[..4000];
        StartedAt = startedAt;
    }

    public Guid ToolId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? ConversationId { get; private set; }

    public string RedactedArguments { get; private set; } = string.Empty;

    public DateTimeOffset StartedAt { get; private set; }

    public ToolExecutionStatus Status { get; private set; }

    /// <summary>Stable reason code for denials and failures (e.g. "tools.permission_denied").</summary>
    public string? ReasonCode { get; private set; }

    public long DurationMs { get; private set; }

    public int RowCount { get; private set; }

    public long ResultBytes { get; private set; }

    public bool Truncated { get; private set; }

    /// <summary>The user explicitly confirmed a write/delete execution.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Records the validated, redacted arguments (text values reduced to their length).</summary>
    public void RecordArguments(string redactedArguments) =>
        RedactedArguments = redactedArguments.Length <= 4000 ? redactedArguments : redactedArguments[..4000];

    public void Complete(ToolExecutionStatus status, string? reasonCode, long durationMs, int rowCount, long resultBytes, bool truncated, bool confirmed)
    {
        Status = status;
        ReasonCode = Text.Optional(reasonCode, nameof(ReasonCode), 64);
        DurationMs = durationMs;
        RowCount = rowCount;
        ResultBytes = resultBytes;
        Truncated = truncated;
        Confirmed = confirmed;
    }
}
