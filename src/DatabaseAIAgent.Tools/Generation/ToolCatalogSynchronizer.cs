using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Tools.Generation;

/// <summary>
/// Keeps tools in step with the discovered catalog. New executable procedures get a tool in <c>PendingReview</c>
/// (never auto-enabled). When a procedure's definition or parameters change, its tool is reclassified and, if it was
/// enabled, disabled until it is reviewed again. Procedures that disappear or lose EXECUTE disable their tool.
/// </summary>
internal sealed class ToolCatalogSynchronizer(
    IDatabaseConnectionRepository connections,
    ISchemaMetadataRepository metadata,
    IToolRepository tools,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<ToolsOptions> options,
    ILogger<ToolCatalogSynchronizer> logger) : IToolCatalogSynchronizer
{
    public async Task<ToolSyncReport> SyncAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await connections.FindAsync(connectionId, cancellationToken)
            ?? throw new InvalidOperationException($"Connection {connectionId} not found.");
        var procedures = await metadata.ListProceduresAsync(connectionId, cancellationToken);
        var existing = (await tools.ListAsync(connectionId, cancellationToken)).ToDictionary(t => t.StoredProcedureId);
        var takenNames = (await tools.ListNamesAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var now = clock.UtcNow;
        int created = 0, updated = 0, disabledChanged = 0, disabledUnavailable = 0, unchanged = 0;

        foreach (var procedure in procedures)
        {
            existing.TryGetValue(procedure.Id, out var tool);
            if (procedure.IsMissing || !procedure.CanExecute)
            {
                if (tool is not null && tool.Status is ToolStatus.Enabled or ToolStatus.PendingReview)
                {
                    tool.Disable(ToolDisabledReason.ProcedureUnavailable);
                    disabledUnavailable++;
                }

                continue;
            }

            var classification = ToolClassifier.Classify(procedure, connection.Provider);
            var (parameters, unsupported) = ToolParameterMapper.Map(procedure, options.Value.DefaultMaxTextLength);

            if (tool is null)
            {
                var name = ToolNaming.UniqueToolName(procedure.SchemaName, procedure.Name, takenNames);
                takenNames.Add(name);
                tool = new AiTool(connectionId, procedure.Id, name, Description(procedure), classification, procedure.DefinitionHash, now);
                tool.SetParameters(parameters, unsupported);
                tool.SetLimits(options.Value.DefaultTimeoutSeconds, options.Value.DefaultMaxRows, tool.MaxResultBytes);
                tools.Add(tool);
                created++;
                continue;
            }

            tool.SourceRestored();
            var definitionChanged = !string.Equals(tool.SourceDefinitionHash, procedure.DefinitionHash, StringComparison.Ordinal);
            var parametersChanged = !SameShape(tool.Parameters, parameters) || tool.UnsupportedReason != unsupported;
            if (!definitionChanged && !parametersChanged)
            {
                unchanged++;
                continue;
            }

            var wasEnabled = tool.Status == ToolStatus.Enabled;
            // A confirmed classification is kept only while the definition is unchanged.
            if (definitionChanged || tool.ClassificationSource != ClassificationSource.AdministratorConfirmed)
            {
                tool.Classify(classification);
            }

            tool.SetParameters(KeepDescriptions(tool.Parameters, parameters), unsupported);
            tool.SourceChanged(procedure.DefinitionHash);
            updated++;
            if (wasEnabled)
            {
                disabledChanged++;
                logger.LogWarning("Tool {ToolName} was disabled because its procedure changed; it needs to be reviewed again", tool.Name);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Synchronized tools for connection {ConnectionId}: {Created} created, {Updated} updated, {DisabledChanged} disabled (changed), {DisabledUnavailable} disabled (unavailable)",
            connectionId, created, updated, disabledChanged, disabledUnavailable);
        return new ToolSyncReport(created, updated, disabledChanged, disabledUnavailable, unchanged);
    }

    private static string Description(StoredProcedure p)
    {
        var purpose = p.Description ?? $"{ToolNaming.Humanize(p.Name)}.";
        var returns = p.ResultColumns.Count > 0
            ? $" Returns columns: {string.Join(", ", p.ResultColumns.Take(15).Select(c => c.Name))}{(p.ResultColumns.Count > 15 ? ", ..." : string.Empty)}."
            : string.Empty;
        var text = $"{purpose.TrimEnd('.')}. Runs the database routine {p.SchemaName}.{p.Name}.{returns}";
        return text.Length <= AiTool.DescriptionMaxLength ? text : text[..AiTool.DescriptionMaxLength];
    }

    private static bool SameShape(List<ToolParameter> current, IReadOnlyList<ToolParameter> generated) =>
        current.Count == generated.Count
        && current.Zip(generated).All(p => p.First.SourceName == p.Second.SourceName && p.First.Type == p.Second.Type
            && p.First.Required == p.Second.Required && p.First.MaxLength == p.Second.MaxLength);

    /// <summary>Administrator-edited parameter descriptions survive regeneration when the parameter still exists.</summary>
    private static IEnumerable<ToolParameter> KeepDescriptions(List<ToolParameter> current, IReadOnlyList<ToolParameter> generated)
    {
        var descriptions = current.ToDictionary(p => p.SourceName, p => p.Description, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in generated)
        {
            if (descriptions.TryGetValue(parameter.SourceName, out var description) && description is not null)
            {
                parameter.Describe(description);
            }

            yield return parameter;
        }
    }
}
