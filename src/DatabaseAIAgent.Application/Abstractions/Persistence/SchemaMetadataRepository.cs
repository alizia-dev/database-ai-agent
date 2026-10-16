using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Application.Abstractions.Persistence;

public sealed record TableSummary(Guid Id, string SchemaName, string Name, TableKind Kind, string? Description, int ColumnCount, long? EstimatedRowCount);

public sealed record ProcedureSummary(
    Guid Id,
    string SchemaName,
    string Name,
    string Signature,
    RoutineKind Kind,
    string? Description,
    int ParameterCount,
    bool CanExecute,
    RoutineBehavior Behavior,
    bool IsMissing);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>Stored catalog metadata for customer databases (no customer data).</summary>
public interface ISchemaMetadataRepository
{
    Task<IReadOnlyList<DatabaseTable>> ListTablesAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredProcedure>> ListProceduresAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<PagedResult<TableSummary>> SearchTablesAsync(Guid connectionId, string? schema, string? search, int page, int pageSize, CancellationToken cancellationToken);

    Task<PagedResult<ProcedureSummary>> SearchProceduresAsync(Guid connectionId, string? schema, string? search, bool includeMissing, int page, int pageSize, CancellationToken cancellationToken);

    Task<DatabaseTable?> FindTableAsync(Guid connectionId, Guid tableId, CancellationToken cancellationToken);

    Task<StoredProcedure?> FindProcedureAsync(Guid connectionId, Guid procedureId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DatabaseRelationship>> ListRelationshipsAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DatabaseSchema>> ListSchemasAsync(Guid connectionId, CancellationToken cancellationToken);

    void AddTable(DatabaseTable table);

    void RemoveTable(DatabaseTable table);

    void AddProcedure(StoredProcedure procedure);

    /// <summary>Replaces all schemas and relationships of the connection (tracked; saved by the caller).</summary>
    Task ReplaceSchemasAndRelationshipsAsync(Guid connectionId, IEnumerable<DatabaseSchema> schemas, IEnumerable<DatabaseRelationship> relationships, CancellationToken cancellationToken);

    Task<SchemaAnalysis?> FindAnalysisAsync(Guid analysisId, CancellationToken cancellationToken);

    Task<SchemaAnalysis?> LatestAnalysisAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<SchemaAnalysis?> ActiveAnalysisAsync(Guid connectionId, CancellationToken cancellationToken);

    void AddAnalysis(SchemaAnalysis analysis);
}
