using System.Text.Json;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Application.Abstractions.Tools;

/// <summary>Creates, updates and disables tools to match a connection's current catalog (after each analysis).</summary>
public interface IToolCatalogSynchronizer
{
    Task<ToolSyncReport> SyncAsync(Guid connectionId, CancellationToken cancellationToken);
}

public sealed record ToolSyncReport(int Created, int Updated, int DisabledChanged, int DisabledUnavailable, int Unchanged);

/// <summary>A request to run a tool, typically issued by the agent on behalf of the signed-in user.</summary>
public sealed record ToolExecutionRequest(string ToolName, JsonElement Arguments, bool Confirmed, Guid? ConversationId = null);

public enum ToolOutcome
{
    Succeeded = 0,

    /// <summary>A write/delete tool: show <see cref="ToolExecutionResult.ConfirmationPrompt"/> and resubmit with Confirmed=true.</summary>
    ConfirmationRequired = 1,
    Denied = 2,
    InvalidArguments = 3,
    Failed = 4,
    TimedOut = 5,
}

/// <summary>
/// Result of the execution pipeline. <see cref="Message"/> is always safe to show to the user and to send to the
/// model; database errors are never passed through.
/// </summary>
public sealed record ToolExecutionResult(
    ToolOutcome Outcome,
    string Message,
    string? ReasonCode,
    Guid? ExecutionId,
    string? ToolName,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyDictionary<string, object?> OutputValues,
    bool Truncated,
    DateTimeOffset? RetrievedAt,
    long DurationMs,
    string? ConfirmationPrompt,
    IReadOnlyDictionary<string, string[]>? ArgumentErrors);

/// <summary>
/// The only path from the AI to a customer database. Order: authentication, permission, subscription and limits,
/// tool enabled, tool role, argument validation, confirmation (writes), timeout, execution, audit.
/// </summary>
public interface IToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>A typed, validated argument bound to a procedure parameter.</summary>
public sealed record ProcedureArgument(string SourceName, ToolParameterType Type, object? Value);

public sealed record ProcedureInvocation(
    Guid ConnectionId,
    StoredProcedure Procedure,
    IReadOnlyList<ProcedureArgument> Arguments,
    bool ReadOnly,
    int TimeoutSeconds,
    int MaxRows,
    int MaxResultBytes);

public sealed record ProcedureResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    IReadOnlyDictionary<string, object?> OutputValues,
    bool Truncated,
    long ResultBytes);

public enum ProcedureFailure
{
    Timeout = 0,
    Unavailable = 1,

    /// <summary>The engine refused a write inside a read-only execution (misclassified procedure).</summary>
    ReadOnlyViolation = 2,
    DatabaseError = 3,
    TooBusy = 4,
}

public sealed class ProcedureExecutionException(ProcedureFailure failure, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public ProcedureExecutionException()
        : this(ProcedureFailure.DatabaseError, "The procedure failed.")
    {
    }

    public ProcedureExecutionException(string message)
        : this(ProcedureFailure.DatabaseError, message)
    {
    }

    public ProcedureExecutionException(string message, Exception innerException)
        : this(ProcedureFailure.DatabaseError, message, innerException)
    {
    }

    public ProcedureFailure Failure { get; } = failure;
}

/// <summary>
/// Executes one stored procedure or function with parameterized arguments, enforcing timeout, row and size limits.
/// Read-only invocations run in a transaction the engine enforces as read-only, or that is always rolled back.
/// </summary>
public interface ICustomerProcedureInvoker
{
    Task<ProcedureResult> InvokeAsync(ProcedureInvocation invocation, CancellationToken cancellationToken);
}
