using System.Text.Json.Nodes;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Common;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;
using DatabaseAIAgent.Tools.Generation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Tools.Management;

public sealed record ToolSummaryDto(
    Guid Id,
    Guid ConnectionId,
    string Name,
    string Description,
    ToolOperation Operation,
    ToolRisk Risk,
    ClassificationSource ClassificationSource,
    ToolStatus Status,
    ToolDisabledReason DisabledReason,
    string ProcedureName,
    int ParameterCount,
    bool Supported,
    DateTimeOffset? ApprovedAt);

public sealed record ToolDetailDto(
    ToolSummaryDto Summary,
    IReadOnlyList<string> ClassificationReasons,
    IReadOnlyList<ToolParameter> Parameters,
    JsonObject ParametersSchema,
    IReadOnlyList<string> AllowedRoles,
    int TimeoutSeconds,
    int MaxRows,
    int MaxResultBytes,
    bool RequiresConfirmation,
    string? UnsupportedReason,
    string? RejectionReason,
    bool CanApprove,
    string? ApprovalBlockedReason,
    bool CanConfirmReadOnly,
    ProcedureInfoDto? Procedure,
    IReadOnlyList<ToolExecutionDto> RecentExecutions);

public sealed record ProcedureInfoDto(string SchemaName, string Name, RoutineKind Kind, string? Description, RoutineBehavior Behavior, bool CanExecute, bool IsMissing, IReadOnlyList<ResultColumn> ResultColumns);

public sealed record ToolExecutionDto(Guid Id, ToolExecutionStatus Status, string? ReasonCode, DateTimeOffset StartedAt, long DurationMs, int RowCount, bool Truncated, bool Confirmed);

public sealed record UpdateToolCommand(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string?>? ParameterDescriptions,
    int TimeoutSeconds,
    int MaxRows,
    int MaxResultKilobytes,
    IReadOnlyList<string> AllowedRoles);

/// <param name="AcknowledgeDataChanges">Required for WRITE/DELETE tools: the approver accepts that the tool changes data.</param>
public sealed record ApproveToolCommand(bool AcknowledgeDataChanges);

public sealed record RejectToolCommand(string? Reason);

/// <param name="Confirmation">Must be <c>true</c>: the administrator confirms the procedure only reads data.</param>
public sealed record ConfirmReadOnlyCommand(bool Confirmation);

/// <summary>Administration of AI tools: review, approve, reject, disable, edit, confirm classification.</summary>
public sealed class ToolManagementService(
    IToolRepository tools,
    IToolExecutionRepository executions,
    ISchemaMetadataRepository metadata,
    IDatabaseConnectionRepository connections,
    IToolCatalogSynchronizer synchronizer,
    IEntitlementService entitlements,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<ToolsOptions> options,
    ILogger<ToolManagementService> logger)
{
    private static readonly Error NotFound = Error.NotFound("tools.not_found", "That tool was not found.");

    public async Task<IReadOnlyList<ToolSummaryDto>> ListAsync(Guid? connectionId, ToolStatus? status, string? search, CancellationToken cancellationToken)
    {
        var list = await tools.ListAsync(connectionId, cancellationToken);
        var procedureNames = new Dictionary<Guid, string>();
        foreach (var connection in list.Select(t => t.ConnectionId).Distinct())
        {
            foreach (var p in await metadata.ListProceduresAsync(connection, cancellationToken))
            {
                procedureNames[p.Id] = $"{p.SchemaName}.{p.Name}";
            }
        }

        return list
            .Where(t => status is null || t.Status == status)
            .Where(t => string.IsNullOrWhiteSpace(search)
                || t.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || t.Description.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(t => Summary(t, procedureNames.GetValueOrDefault(t.StoredProcedureId, "(unknown)")))
            .ToList();
    }

    public async Task<Result<ToolDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var tool = await tools.FindAsync(id, cancellationToken);
        return tool is null ? NotFound : await DetailAsync(tool, cancellationToken);
    }

    public async Task<Result<ToolSyncReport>> GenerateAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        if (await connections.FindAsync(connectionId, cancellationToken) is null)
        {
            return Error.NotFound("database.connection_not_found", "That database connection was not found.");
        }

        return await synchronizer.SyncAsync(connectionId, cancellationToken);
    }

    public async Task<Result<ToolDetailDto>> UpdateAsync(Guid id, UpdateToolCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tool = await tools.FindAsync(id, cancellationToken);
        if (tool is null)
        {
            return NotFound;
        }

        var name = command.Name.Trim();
        if (name != tool.Name && (await tools.FindByNameAsync(name, cancellationToken)) is not null)
        {
            return Error.Conflict("tools.name_taken", "Another tool already uses this name.");
        }

        try
        {
            tool.Rename(name);
            tool.Describe(command.Description);
            tool.SetLimits(command.TimeoutSeconds, command.MaxRows, command.MaxResultKilobytes * 1024);
            tool.SetAllowedRoles(command.AllowedRoles);
            if (command.ParameterDescriptions is not null)
            {
                foreach (var parameter in tool.Parameters)
                {
                    if (command.ParameterDescriptions.TryGetValue(parameter.Name, out var description))
                    {
                        parameter.Describe(description);
                    }
                }

                tool.SetParameters(tool.Parameters.ToList(), tool.UnsupportedReason);
            }
        }
        catch (DomainException ex)
        {
            unitOfWork.DiscardChanges();
            return Error.Validation("tools.invalid", ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} edited tool {ToolName}", currentUser.UserId, tool.Name);
        return await DetailAsync(tool, cancellationToken);
    }

    public async Task<Result<ToolDetailDto>> ApproveAsync(Guid id, ApproveToolCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tool = await tools.FindAsync(id, cancellationToken);
        if (tool is null)
        {
            return NotFound;
        }

        var procedure = await metadata.FindProcedureAsync(tool.ConnectionId, tool.StoredProcedureId, cancellationToken);
        var blocked = await ApprovalBlockedReasonAsync(tool, procedure, cancellationToken);
        if (blocked is not null)
        {
            return blocked;
        }

        if (tool.RequiresConfirmation && !command.AcknowledgeDataChanges)
        {
            return Error.Validation("tools.acknowledgement_required", "Confirm that you understand this tool changes data in your database.");
        }

        tool.SourceChanged(procedure!.DefinitionHash);
        tool.Approve(currentUser.RequiredUserId, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} approved {Operation} tool {ToolName}", currentUser.UserId, tool.Operation, tool.Name);
        return await DetailAsync(tool, cancellationToken);
    }

    public async Task<Result<ToolDetailDto>> DisableAsync(Guid id, CancellationToken cancellationToken) =>
        await ChangeAsync(id, tool => tool.Disable(ToolDisabledReason.ByAdministrator), "disabled", cancellationToken);

    public async Task<Result<ToolDetailDto>> RejectAsync(Guid id, RejectToolCommand command, CancellationToken cancellationToken) =>
        await ChangeAsync(id, tool => tool.Reject(command?.Reason), "rejected", cancellationToken);

    public async Task<Result<ToolDetailDto>> ReopenAsync(Guid id, CancellationToken cancellationToken) =>
        await ChangeAsync(id, tool => tool.Reopen(), "reopened", cancellationToken);

    /// <summary>
    /// For routines whose definition is not visible: an administrator confirms the routine only reads data. Executions
    /// still run in a read-only transaction, so a wrong confirmation cannot change data.
    /// </summary>
    public async Task<Result<ToolDetailDto>> ConfirmReadOnlyAsync(Guid id, ConfirmReadOnlyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var tool = await tools.FindAsync(id, cancellationToken);
        if (tool is null)
        {
            return NotFound;
        }

        if (tool.ClassificationSource != ClassificationSource.Unverified)
        {
            return Error.Validation("tools.classification_verified", "This tool's classification was determined from its definition and cannot be overridden.");
        }

        if (!command.Confirmation)
        {
            return Error.Validation("tools.confirmation_required", "Confirm that this procedure only reads data.");
        }

        tool.Classify(ToolClassifier.ConfirmedReadOnly(currentUser.Email ?? "an administrator", clock.UtcNow));
        tool.SetAllowedRoles(AiTool.DefaultRoles(ToolOperation.Read));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} confirmed tool {ToolName} as read-only", currentUser.UserId, tool.Name);
        return await DetailAsync(tool, cancellationToken);
    }

    private async Task<Result<ToolDetailDto>> ChangeAsync(Guid id, Action<AiTool> change, string action, CancellationToken cancellationToken)
    {
        var tool = await tools.FindAsync(id, cancellationToken);
        if (tool is null)
        {
            return NotFound;
        }

        change(tool);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} {Action} tool {ToolName}", currentUser.UserId, action, tool.Name);
        return await DetailAsync(tool, cancellationToken);
    }

    private async Task<Error?> ApprovalBlockedReasonAsync(AiTool tool, StoredProcedure? procedure, CancellationToken cancellationToken)
    {
        if (tool.Status == ToolStatus.Enabled)
        {
            return Error.Conflict("tools.already_enabled", "This tool is already enabled.");
        }

        if (tool.Status == ToolStatus.Rejected)
        {
            return Error.Validation("tools.rejected", "Reopen this rejected tool before approving it.");
        }

        if (tool.UnsupportedReason is not null)
        {
            return Error.Validation("tools.unsupported", tool.UnsupportedReason);
        }

        if (tool.Operation == ToolOperation.Admin)
        {
            return Error.Forbidden("tools.admin_not_allowed", "Tools that change the database schema or permissions cannot be enabled.");
        }

        if (tool.Operation != ToolOperation.Read && !options.Value.AllowWriteTools)
        {
            return Error.Forbidden("tools.write_disabled", "Tools that change data are disabled for this workspace. Only read-only tools can be enabled.");
        }

        if (procedure is null || procedure.IsMissing || !procedure.CanExecute)
        {
            return Error.Validation("tools.procedure_unavailable", "The procedure no longer exists or the connection's account cannot execute it.");
        }

        var plan = await entitlements.GetCurrentAsync(cancellationToken);
        if (!plan.HasFeature(PlanFeatures.ProductionAccess))
        {
            return Error.Forbidden("billing.upgrade_required", "Your current plan does not include enabling tools on your own database.");
        }

        if (plan.Limits.MaxApprovedTools is { } max && await tools.CountEnabledAsync(cancellationToken) >= max)
        {
            return Error.Forbidden("billing.limit_reached", $"Your plan allows {max} enabled tools. Disable a tool or upgrade your plan.");
        }

        return null;
    }

    private async Task<ToolDetailDto> DetailAsync(AiTool tool, CancellationToken cancellationToken)
    {
        var procedure = await metadata.FindProcedureAsync(tool.ConnectionId, tool.StoredProcedureId, cancellationToken);
        var blocked = tool.Status is ToolStatus.Enabled ? null : await ApprovalBlockedReasonAsync(tool, procedure, cancellationToken);
        var recent = await executions.ListRecentAsync(tool.Id, 20, cancellationToken);
        return new ToolDetailDto(
            Summary(tool, procedure is null ? "(unknown)" : $"{procedure.SchemaName}.{procedure.Name}"),
            tool.ClassificationReasons,
            tool.Parameters,
            ToolJsonSchema.For(tool),
            tool.AllowedRoles.Select(r => r.Role).ToList(),
            tool.TimeoutSeconds,
            tool.MaxRows,
            tool.MaxResultBytes,
            tool.RequiresConfirmation,
            tool.UnsupportedReason,
            tool.RejectionReason,
            CanApprove: tool.Status != ToolStatus.Enabled && blocked is null,
            ApprovalBlockedReason: blocked?.Message,
            CanConfirmReadOnly: tool.ClassificationSource == ClassificationSource.Unverified,
            procedure is null ? null : new ProcedureInfoDto(procedure.SchemaName, procedure.Name, procedure.Kind, procedure.Description, procedure.Behavior, procedure.CanExecute, procedure.IsMissing, procedure.ResultColumns),
            recent.Select(e => new ToolExecutionDto(e.Id, e.Status, e.ReasonCode, e.StartedAt, e.DurationMs, e.RowCount, e.Truncated, e.Confirmed)).ToList());
    }

    private static ToolSummaryDto Summary(AiTool t, string procedureName) => new(
        t.Id, t.ConnectionId, t.Name, t.Description, t.Operation, t.Risk, t.ClassificationSource, t.Status, t.DisabledReason,
        procedureName, t.Parameters.Count, t.UnsupportedReason is null, t.ApprovedAt);
}
