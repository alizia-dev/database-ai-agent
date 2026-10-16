using System.Data.Common;
using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Application.Abstractions.Databases;

/// <summary>Bounds for one discovery run, so a huge catalog cannot overload the customer database or the platform.</summary>
public sealed record DiscoveryLimits(int MaxTables, int MaxProcedures, int CommandTimeoutSeconds);

/// <summary>
/// Reads catalog metadata (never table rows) from a customer database: schemas, tables, views, columns, keys,
/// indexes, relationships, routines, parameters and result columns.
/// </summary>
public interface ISchemaDiscoverer
{
    DatabaseProviderKind Kind { get; }

    Task<DiscoveredDatabase> DiscoverAsync(DbConnection connection, DiscoveryLimits limits, CancellationToken cancellationToken);
}

public sealed record DiscoveredDatabase(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<DiscoveredTable> Tables,
    IReadOnlyList<DiscoveredRelationship> Relationships,
    IReadOnlyList<DiscoveredRoutine> Routines,
    IReadOnlyList<string> Warnings);

public sealed record DiscoveredTable(
    string Schema,
    string Name,
    TableKind Kind,
    string? Description,
    long? EstimatedRowCount,
    IReadOnlyList<DiscoveredColumn> Columns,
    IReadOnlyList<DiscoveredIndex> Indexes);

public sealed record DiscoveredColumn(
    string Name,
    int Ordinal,
    string DataType,
    int? MaxLength,
    int? Precision,
    int? Scale,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsIdentity,
    string? Description);

public sealed record DiscoveredIndex(string Name, bool IsUnique, bool IsPrimaryKey, IReadOnlyList<string> Columns);

public sealed record DiscoveredRelationship(
    string Name,
    string FromSchema,
    string FromTable,
    IReadOnlyList<string> FromColumns,
    string ToSchema,
    string ToTable,
    IReadOnlyList<string> ToColumns);

/// <summary>A routine as discovered. <see cref="Definition"/> is used for behaviour analysis and then discarded.</summary>
public sealed record DiscoveredRoutine(
    string Schema,
    string Name,
    string Signature,
    RoutineKind Kind,
    string? Description,
    string? ReturnType,
    bool CanExecute,
    string? Definition,
    string? DeclaredDataAccess,
    double? AverageDurationMs,
    IReadOnlyList<DiscoveredParameter> Parameters,
    IReadOnlyList<DiscoveredResultColumn> ResultColumns);

public sealed record DiscoveredParameter(
    string Name,
    int Ordinal,
    string DataType,
    int? MaxLength,
    int? Precision,
    int? Scale,
    ParameterDirection Direction,
    bool HasDefault);

public sealed record DiscoveredResultColumn(string Name, int Ordinal, string DataType, bool IsNullable);
