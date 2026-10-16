using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Database.Discovery;

public sealed class DiscoveryOptions
{
    public const string SectionName = "SchemaDiscovery";

    [Range(1, 100_000)]
    public int MaxTables { get; set; } = 5000;

    [Range(1, 100_000)]
    public int MaxProcedures { get; set; } = 5000;

    [Range(5, 1800)]
    public int CommandTimeoutSeconds { get; set; } = 120;
}

public sealed record AnalysisDto(
    Guid Id,
    AnalysisStatus Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureMessage,
    AnalysisCounts Counts,
    IReadOnlyList<string> Warnings);

public sealed record SchemaOverviewDto(AnalysisDto? LatestAnalysis, IReadOnlyList<SchemaSummaryDto> Schemas);

public sealed record SchemaSummaryDto(string Name, int Tables, int Views, int Procedures);

public sealed record TableDetailDto(
    Guid Id,
    string SchemaName,
    string Name,
    TableKind Kind,
    string? Description,
    long? EstimatedRowCount,
    IReadOnlyList<DatabaseColumn> Columns,
    IReadOnlyList<DatabaseIndex> Indexes,
    IReadOnlyList<DatabaseRelationship> References,
    IReadOnlyList<DatabaseRelationship> ReferencedBy,
    DateTimeOffset LastSeenAt);

public sealed record ProcedureDetailDto(
    Guid Id,
    string SchemaName,
    string Name,
    string Signature,
    RoutineKind Kind,
    string? Description,
    string? ReturnType,
    bool CanExecute,
    RoutineBehavior Behavior,
    bool IsReadOnly,
    string? DeclaredDataAccess,
    double? AverageDurationMs,
    IReadOnlyList<ProcedureParameter> Parameters,
    IReadOnlyList<ResultColumn> ResultColumns,
    bool IsMissing,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Schema analysis: requests run in the background (never inside an HTTP request) and store metadata only.
/// Also serves the stored catalog for the schema browser.
/// </summary>
public sealed class SchemaAnalysisService(
    IDatabaseConnectionRepository connections,
    ISchemaMetadataRepository metadata,
    IEntitlementService entitlements,
    IJobQueue jobs,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<SchemaAnalysisService> logger)
{
    public const string JobType = "schema-analysis";

    private static readonly Error ConnectionNotFound = Error.NotFound("database.connection_not_found", "That database connection was not found.");

    public async Task<Result<AnalysisDto>> StartAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        if (!(await entitlements.GetCurrentAsync(cancellationToken)).HasFeature(PlanFeatures.ProductionAccess))
        {
            return Error.Forbidden("billing.upgrade_required", "Your current plan does not include analyzing your own database. Choose a subscription plan to continue.");
        }

        if (await connections.FindAsync(connectionId, cancellationToken) is null)
        {
            return ConnectionNotFound;
        }

        if (await metadata.ActiveAnalysisAsync(connectionId, cancellationToken) is not null)
        {
            return Error.Conflict("database.analysis_in_progress", "An analysis of this database is already running.");
        }

        var analysis = new SchemaAnalysis(connectionId, currentUser.RequiredUserId, clock.UtcNow);
        analysis.AttachJob(jobs.Enqueue(JobType, new SchemaAnalysisJob(analysis.Id), maxAttempts: 2));
        metadata.AddAnalysis(analysis);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {UserId} queued schema analysis {AnalysisId} for connection {ConnectionId}", currentUser.UserId, analysis.Id, connectionId);
        return ToDto(analysis);
    }

    public async Task<Result<SchemaOverviewDto>> GetOverviewAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        if (await connections.FindAsync(connectionId, cancellationToken) is null)
        {
            return ConnectionNotFound;
        }

        var latest = await metadata.LatestAnalysisAsync(connectionId, cancellationToken);
        var tables = await AllTablesAsync(connectionId, cancellationToken);
        var procedures = await AllProceduresAsync(connectionId, cancellationToken);
        var schemas = (await metadata.ListSchemasAsync(connectionId, cancellationToken))
            .Select(s => new SchemaSummaryDto(
                s.Name,
                tables.Count(t => t.SchemaName == s.Name && t.Kind == TableKind.Table),
                tables.Count(t => t.SchemaName == s.Name && t.Kind == TableKind.View),
                procedures.Count(p => p.SchemaName == s.Name)))
            .ToList();

        return new SchemaOverviewDto(latest is null ? null : ToDto(latest), schemas);
    }

    public async Task<Result<PagedResult<TableSummary>>> SearchTablesAsync(Guid connectionId, string? schema, string? search, int page, int pageSize, CancellationToken cancellationToken) =>
        await connections.FindAsync(connectionId, cancellationToken) is null
            ? ConnectionNotFound
            : await metadata.SearchTablesAsync(connectionId, schema, search, page, pageSize, cancellationToken);

    public async Task<Result<PagedResult<ProcedureSummary>>> SearchProceduresAsync(Guid connectionId, string? schema, string? search, bool includeMissing, int page, int pageSize, CancellationToken cancellationToken) =>
        await connections.FindAsync(connectionId, cancellationToken) is null
            ? ConnectionNotFound
            : await metadata.SearchProceduresAsync(connectionId, schema, search, includeMissing, page, pageSize, cancellationToken);

    public async Task<Result<TableDetailDto>> GetTableAsync(Guid connectionId, Guid tableId, CancellationToken cancellationToken)
    {
        var table = await metadata.FindTableAsync(connectionId, tableId, cancellationToken);
        if (table is null)
        {
            return Error.NotFound("database.table_not_found", "That table was not found.");
        }

        var relationships = await metadata.ListRelationshipsAsync(connectionId, cancellationToken);
        return new TableDetailDto(
            table.Id,
            table.SchemaName,
            table.Name,
            table.Kind,
            table.Description,
            table.EstimatedRowCount,
            table.Columns,
            table.Indexes,
            relationships.Where(r => r.FromSchema == table.SchemaName && r.FromTable == table.Name).ToList(),
            relationships.Where(r => r.ToSchema == table.SchemaName && r.ToTable == table.Name).ToList(),
            table.LastSeenAt);
    }

    public async Task<Result<ProcedureDetailDto>> GetProcedureAsync(Guid connectionId, Guid procedureId, CancellationToken cancellationToken)
    {
        var p = await metadata.FindProcedureAsync(connectionId, procedureId, cancellationToken);
        return p is null
            ? Error.NotFound("database.procedure_not_found", "That procedure was not found.")
            : new ProcedureDetailDto(
                p.Id, p.SchemaName, p.Name, p.Signature, p.Kind, p.Description, p.ReturnType, p.CanExecute, p.Behavior, p.Behavior.IsReadOnly,
                p.DeclaredDataAccess, p.AverageDurationMs, p.Parameters, p.ResultColumns, p.IsMissing, p.FirstSeenAt, p.LastSeenAt);
    }

    internal static AnalysisDto ToDto(SchemaAnalysis a) => new(
        a.Id,
        a.Status,
        a.QueuedAt,
        a.StartedAt,
        a.CompletedAt,
        a.Status == AnalysisStatus.Failed ? ConnectionMessages.For(a.Failure) : null,
        new AnalysisCounts(a.SchemaCount, a.TableCount, a.ViewCount, a.ColumnCount, a.RelationshipCount, a.ProcedureCount, a.NewProcedureCount, a.MissingProcedureCount),
        a.Warnings);

    private async Task<List<TableSummary>> AllTablesAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var all = new List<TableSummary>();
        for (var page = 1; ; page++)
        {
            var result = await metadata.SearchTablesAsync(connectionId, null, null, page, 200, cancellationToken);
            all.AddRange(result.Items);
            if (all.Count >= result.Total || result.Items.Count == 0)
            {
                return all;
            }
        }
    }

    private async Task<List<ProcedureSummary>> AllProceduresAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var all = new List<ProcedureSummary>();
        for (var page = 1; ; page++)
        {
            var result = await metadata.SearchProceduresAsync(connectionId, null, null, includeMissing: false, page, 200, cancellationToken);
            all.AddRange(result.Items);
            if (all.Count >= result.Total || result.Items.Count == 0)
            {
                return all;
            }
        }
    }
}

public sealed record SchemaAnalysisJob(Guid AnalysisId);

/// <summary>
/// Runs a schema analysis in the Workers host: opens the customer database (credentials decrypted, host re-checked),
/// reads catalog metadata within limits, and merges it into the stored catalog in a single save. Connection problems
/// end the analysis as failed with a safe reason rather than failing the job.
/// </summary>
internal sealed class SchemaAnalysisJobHandler(
    ISchemaMetadataRepository metadata,
    IDatabaseConnectionRepository connections,
    ICustomerDatabaseConnector connector,
    IEnumerable<ISchemaDiscoverer> discoverers,
    SchemaCatalogWriter writer,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<DiscoveryOptions> options,
    IEnumerable<DatabaseAIAgent.Application.Abstractions.Tools.IToolCatalogSynchronizer> toolSynchronizers,
    ILogger<SchemaAnalysisJobHandler> logger) : IJobHandler
{
    public string JobType => SchemaAnalysisService.JobType;

    public TimeSpan LockDuration => TimeSpan.FromMinutes(30);

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var job = context.Read<SchemaAnalysisJob>();
        var analysis = await metadata.FindAnalysisAsync(job.AnalysisId, cancellationToken);
        if (analysis is null || !analysis.IsActive)
        {
            return;
        }

        var connection = await connections.FindAsync(analysis.ConnectionId, cancellationToken);
        if (connection is null)
        {
            analysis.Fail(clock.UtcNow, ConnectionFailureKind.Unknown);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        analysis.Start(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        DiscoveredDatabase discovered;
        try
        {
            var discoverer = discoverers.Single(d => d.Kind == connection.Provider);
            var settings = options.Value;
            await using var db = await connector.OpenAsync(connection.Id, cancellationToken);
            discovered = await discoverer.DiscoverAsync(db, new DiscoveryLimits(settings.MaxTables, settings.MaxProcedures, settings.CommandTimeoutSeconds), cancellationToken);
        }
        catch (CustomerDatabaseUnavailableException ex)
        {
            await FailAsync(analysis.Id, ex.Failure, cancellationToken);
            return;
        }
        catch (DbException ex)
        {
            logger.LogWarning("Schema discovery for connection {ConnectionId} failed ({ErrorType})", connection.Id, ex.GetType().Name);
            await FailAsync(analysis.Id, ConnectionFailureKind.PermissionDenied, cancellationToken);
            return;
        }

        var counts = await writer.ApplyAsync(connection, discovered, clock.UtcNow, cancellationToken);
        analysis.Complete(clock.UtcNow, counts, discovered.Warnings);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Schema analysis {AnalysisId} finished: {Tables} tables, {Views} views, {Procedures} routines ({New} new, {Missing} missing)",
            analysis.Id, counts.Tables, counts.Views, counts.Procedures, counts.NewProcedures, counts.MissingProcedures);

        // Generate tool drafts for new procedures and disable tools whose procedure changed or disappeared.
        foreach (var synchronizer in toolSynchronizers)
        {
            try
            {
                await synchronizer.SyncAsync(connection.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tool synchronization after analysis {AnalysisId} failed", analysis.Id);
                unitOfWork.DiscardChanges();
            }
        }
    }

    private async Task FailAsync(Guid analysisId, ConnectionFailureKind failure, CancellationToken cancellationToken)
    {
        unitOfWork.DiscardChanges();
        var analysis = await metadata.FindAnalysisAsync(analysisId, cancellationToken);
        if (analysis is not null)
        {
            analysis.Fail(clock.UtcNow, failure);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
