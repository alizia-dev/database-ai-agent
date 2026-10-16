using System.Diagnostics;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Authorization;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Tools;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Tools.Execution;

/// <summary>
/// The execution pipeline: authentication → permission → subscription and monthly limit → tool enabled → tool role →
/// argument validation → confirmation (writes) → timeout → execution → audit. Every attempt that reaches the tool
/// (including denials) is recorded. The model's request is never trusted: identity and permissions come from the
/// authenticated context, and only the approved procedure behind an enabled tool can run.
/// </summary>
internal sealed class ToolExecutor(
    ICurrentUser currentUser,
    IPermissionService permissions,
    IEntitlementService entitlements,
    IToolRepository tools,
    IToolExecutionRepository executions,
    ISchemaMetadataRepository metadata,
    IDatabaseConnectionRepository connections,
    ICustomerProcedureInvoker invoker,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ToolExecutor> logger) : IToolExecutor
{
    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Authentication.
        if (currentUser.UserId is not { } userId)
        {
            return Denied("tools.unauthenticated", "Sign in to use this tool.", null);
        }

        var tool = AiTool.IsValidName(request.ToolName ?? string.Empty) ? await tools.FindByNameAsync(request.ToolName!, cancellationToken) : null;
        if (tool is null)
        {
            return Denied("tools.not_found", "That tool does not exist.", null);
        }

        var audit = new ToolExecution(tool.Id, userId, request.ConversationId, "{}", clock.UtcNow);
        var stopwatch = Stopwatch.StartNew();

        // 2. Authorization (user permission).
        if (!await permissions.HasPermissionAsync(userId, Permissions.ToolsExecute, cancellationToken))
        {
            return await DenyAsync(audit, tool, "tools.permission_denied", "You do not have permission to run AI tools.");
        }

        // 3. Subscription and plan limit.
        var plan = await entitlements.GetCurrentAsync(cancellationToken);
        if (!plan.HasFeature(PlanFeatures.ProductionAccess))
        {
            return await DenyAsync(audit, tool, "billing.upgrade_required", "Your current plan does not include running tools on your database.");
        }

        if (plan.Limits.MaxToolExecutionsPerMonth is { } monthlyLimit)
        {
            var now = clock.UtcNow;
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            if (await executions.CountExecutedSinceAsync(monthStart, cancellationToken) >= monthlyLimit)
            {
                return await DenyAsync(audit, tool, "billing.limit_reached", "Monthly tool execution limit reached.");
            }
        }

        // 4. Tool enabled (and its procedure still present).
        var procedure = await metadata.FindProcedureAsync(tool.ConnectionId, tool.StoredProcedureId, cancellationToken);
        if (!tool.IsAvailable || procedure is null || procedure.IsMissing || !procedure.CanExecute)
        {
            return await DenyAsync(audit, tool, "tools.disabled", "This tool is not enabled.");
        }

        // 5. Tool permission (role allowlist), from the database rather than the token.
        var role = await permissions.GetRoleAsync(userId, cancellationToken);
        if (role is null || !tool.AllowsRole(role))
        {
            return await DenyAsync(audit, tool, "tools.role_not_allowed", "Your role is not allowed to use this tool.");
        }

        // 6. Argument validation.
        var (validated, errors) = ToolArgumentValidator.Validate(tool, request.Arguments);
        if (validated is null)
        {
            await RecordAsync(audit, ToolExecutionStatus.Denied, "tools.invalid_arguments", stopwatch, null, confirmed: false);
            return new ToolExecutionResult(ToolOutcome.InvalidArguments, "The tool arguments are not valid.", "tools.invalid_arguments", audit.Id, tool.Name,
                [], [], new Dictionary<string, object?>(), false, null, 0, null, errors);
        }

        // 6b. Writes and deletes require the user's explicit confirmation for every execution.
        if (tool.RequiresConfirmation && !request.Confirmed)
        {
            var connection = await connections.FindAsync(tool.ConnectionId, cancellationToken);
            var prompt = ConfirmationPrompt(tool, connection?.Name, validated.Summary);
            await RecordAsync(audit, ToolExecutionStatus.ConfirmationRequired, "tools.confirmation_required", stopwatch, null, confirmed: false, validated.RedactedJson);
            return new ToolExecutionResult(ToolOutcome.ConfirmationRequired, prompt, "tools.confirmation_required", audit.Id, tool.Name,
                [], [], new Dictionary<string, object?>(), false, null, 0, prompt, null);
        }

        // 7-8. Timeout and execution (read tools in a read-only transaction).
        try
        {
            var result = await invoker.InvokeAsync(new ProcedureInvocation(
                tool.ConnectionId, procedure, validated.Arguments, ReadOnly: tool.Operation == ToolOperation.Read,
                tool.TimeoutSeconds, tool.MaxRows, tool.MaxResultBytes), cancellationToken);

            await RecordAsync(audit, ToolExecutionStatus.Succeeded, null, stopwatch, result, tool.RequiresConfirmation, validated.RedactedJson);
            var message = result.Truncated
                ? $"Returned {result.Rows.Count} rows (more rows exist; results were limited)."
                : $"Returned {result.Rows.Count} row{(result.Rows.Count == 1 ? string.Empty : "s")}.";
            return new ToolExecutionResult(ToolOutcome.Succeeded, message, null, audit.Id, tool.Name, result.Columns, result.Rows,
                result.OutputValues, result.Truncated, clock.UtcNow, stopwatch.ElapsedMilliseconds, null, null);
        }
        catch (ProcedureExecutionException ex)
        {
            // 9. Audit failures too; the message is safe (no driver text).
            var timedOut = ex.Failure == ProcedureFailure.Timeout;
            var code = ex.Failure switch
            {
                ProcedureFailure.Timeout => "tools.timeout",
                ProcedureFailure.Unavailable => "tools.database_unavailable",
                ProcedureFailure.ReadOnlyViolation => "tools.read_only_violation",
                ProcedureFailure.TooBusy => "tools.busy",
                _ => "tools.database_error",
            };
            if (ex.Failure == ProcedureFailure.ReadOnlyViolation)
            {
                logger.LogWarning("Read-only tool {ToolName} attempted to modify data; it should be reclassified", tool.Name);
            }

            await RecordAsync(audit, timedOut ? ToolExecutionStatus.TimedOut : ToolExecutionStatus.Failed, code, stopwatch, null, tool.RequiresConfirmation, validated.RedactedJson);
            return new ToolExecutionResult(timedOut ? ToolOutcome.TimedOut : ToolOutcome.Failed, ex.Message, code, audit.Id, tool.Name,
                [], [], new Dictionary<string, object?>(), false, null, stopwatch.ElapsedMilliseconds, null, null);
        }
    }

    private static string ConfirmationPrompt(AiTool tool, string? connectionName, IReadOnlyList<(string Name, string Display)> arguments)
    {
        var action = tool.Operation == ToolOperation.Delete ? "delete data" : "change data";
        var details = arguments.Count == 0 ? "no arguments" : string.Join(", ", arguments.Select(a => $"{a.Name} = {a.Display}"));
        return $"You're about to run \"{tool.Name}\" on {connectionName ?? "your database"} with {details}. This will {action} in your database. Confirm to continue.";
    }

    private async Task<ToolExecutionResult> DenyAsync(ToolExecution audit, AiTool tool, string code, string message)
    {
        await RecordAsync(audit, ToolExecutionStatus.Denied, code, Stopwatch.StartNew(), null, confirmed: false);
        logger.LogInformation("Tool {ToolName} denied for user {UserId}: {Code}", tool.Name, audit.UserId, code);
        return new ToolExecutionResult(ToolOutcome.Denied, message, code, audit.Id, tool.Name, [], [], new Dictionary<string, object?>(), false, null, 0, null, null);
    }

    private static ToolExecutionResult Denied(string code, string message, string? toolName) =>
        new(ToolOutcome.Denied, message, code, null, toolName, [], [], new Dictionary<string, object?>(), false, null, 0, null, null);

    private async Task RecordAsync(ToolExecution audit, ToolExecutionStatus status, string? code, Stopwatch stopwatch, ProcedureResult? result, bool confirmed, string? redactedArguments = null)
    {
        if (redactedArguments is not null)
        {
            audit.RecordArguments(redactedArguments);
        }

        audit.Complete(status, code, stopwatch.ElapsedMilliseconds, result?.Rows.Count ?? 0, result?.ResultBytes ?? 0, result?.Truncated ?? false, confirmed);
        executions.Add(audit);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
    }
}
