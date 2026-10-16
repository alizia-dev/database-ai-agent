using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence.Repositories;

internal sealed class SchemaMetadataRepository(PlatformDbContext db) : ISchemaMetadataRepository
{
    private const int MaxPageSize = 200;

    public async Task<IReadOnlyList<DatabaseTable>> ListTablesAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.DatabaseTables.Where(t => t.ConnectionId == connectionId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<StoredProcedure>> ListProceduresAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.StoredProcedures.Where(p => p.ConnectionId == connectionId).ToListAsync(cancellationToken);

    public async Task<PagedResult<TableSummary>> SearchTablesAsync(Guid connectionId, string? schema, string? search, int page, int pageSize, CancellationToken cancellationToken)
    {
        // Scalar projection (no JSON children); filtering in memory keeps search case-insensitive on every provider.
        var rows = await db.DatabaseTables.AsNoTracking()
            .Where(t => t.ConnectionId == connectionId && (schema == null || t.SchemaName == schema))
            .Select(t => new TableSummary(t.Id, t.SchemaName, t.Name, t.Kind, t.Description, t.ColumnCount, t.EstimatedRowCount))
            .ToListAsync(cancellationToken);

        var filtered = rows
            .Where(t => Matches(search, t.SchemaName, t.Name, t.Description))
            .OrderBy(t => t.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Page(filtered, page, pageSize);
    }

    public async Task<PagedResult<ProcedureSummary>> SearchProceduresAsync(Guid connectionId, string? schema, string? search, bool includeMissing, int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await db.StoredProcedures.AsNoTracking()
            .Where(p => p.ConnectionId == connectionId && (schema == null || p.SchemaName == schema) && (includeMissing || !p.IsMissing))
            .Select(p => new ProcedureSummary(p.Id, p.SchemaName, p.Name, p.Signature, p.Kind, p.Description, p.ParameterCount, p.CanExecute, p.Behavior, p.IsMissing))
            .ToListAsync(cancellationToken);

        var filtered = rows
            .Where(p => Matches(search, p.SchemaName, p.Name, p.Description))
            .OrderBy(p => p.SchemaName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Page(filtered, page, pageSize);
    }

    public Task<DatabaseTable?> FindTableAsync(Guid connectionId, Guid tableId, CancellationToken cancellationToken) =>
        db.DatabaseTables.AsNoTracking().SingleOrDefaultAsync(t => t.ConnectionId == connectionId && t.Id == tableId, cancellationToken);

    public Task<StoredProcedure?> FindProcedureAsync(Guid connectionId, Guid procedureId, CancellationToken cancellationToken) =>
        db.StoredProcedures.AsNoTracking().SingleOrDefaultAsync(p => p.ConnectionId == connectionId && p.Id == procedureId, cancellationToken);

    public async Task<IReadOnlyList<DatabaseRelationship>> ListRelationshipsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.DatabaseRelationships.AsNoTracking().Where(r => r.ConnectionId == connectionId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DatabaseSchema>> ListSchemasAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.DatabaseSchemas.AsNoTracking().Where(s => s.ConnectionId == connectionId).OrderBy(s => s.Name).ToListAsync(cancellationToken);

    public void AddTable(DatabaseTable table) => db.DatabaseTables.Add(table);

    public void RemoveTable(DatabaseTable table) => db.DatabaseTables.Remove(table);

    public void AddProcedure(StoredProcedure procedure) => db.StoredProcedures.Add(procedure);

    public async Task ReplaceSchemasAndRelationshipsAsync(Guid connectionId, IEnumerable<DatabaseSchema> schemas, IEnumerable<DatabaseRelationship> relationships, CancellationToken cancellationToken)
    {
        db.DatabaseSchemas.RemoveRange(await db.DatabaseSchemas.Where(s => s.ConnectionId == connectionId).ToListAsync(cancellationToken));
        db.DatabaseRelationships.RemoveRange(await db.DatabaseRelationships.Where(r => r.ConnectionId == connectionId).ToListAsync(cancellationToken));
        db.DatabaseSchemas.AddRange(schemas);
        db.DatabaseRelationships.AddRange(relationships);
    }

    public Task<SchemaAnalysis?> FindAnalysisAsync(Guid analysisId, CancellationToken cancellationToken) =>
        db.SchemaAnalyses.SingleOrDefaultAsync(a => a.Id == analysisId, cancellationToken);

    public Task<SchemaAnalysis?> LatestAnalysisAsync(Guid connectionId, CancellationToken cancellationToken) =>
        db.SchemaAnalyses.AsNoTracking().Where(a => a.ConnectionId == connectionId).OrderByDescending(a => a.QueuedAt).FirstOrDefaultAsync(cancellationToken);

    public Task<SchemaAnalysis?> ActiveAnalysisAsync(Guid connectionId, CancellationToken cancellationToken) =>
        db.SchemaAnalyses.AsNoTracking()
            .Where(a => a.ConnectionId == connectionId && (a.Status == AnalysisStatus.Queued || a.Status == AnalysisStatus.Running))
            .FirstOrDefaultAsync(cancellationToken);

    public void AddAnalysis(SchemaAnalysis analysis) => db.SchemaAnalyses.Add(analysis);

    private static bool Matches(string? search, string schema, string name, string? description) =>
        string.IsNullOrWhiteSpace(search)
        || name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || $"{schema}.{name}".Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || (description?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static PagedResult<T> Page<T>(List<T> items, int page, int pageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var number = Math.Max(page, 1);
        return new PagedResult<T>(items.Skip((number - 1) * size).Take(size).ToList(), items.Count, number, size);
    }
}
