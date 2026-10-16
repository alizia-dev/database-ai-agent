using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Databases;

public enum TableKind
{
    Table = 0,
    View = 1,
}

public enum RoutineKind
{
    Procedure = 0,
    ScalarFunction = 1,
    TableFunction = 2,
}

public enum ParameterDirection
{
    In = 0,
    Out = 1,
    InOut = 2,
}

/// <summary>A schema (namespace) in a customer database.</summary>
public sealed class DatabaseSchema : Entity
{
    private DatabaseSchema()
    {
    }

    public DatabaseSchema(Guid connectionId, string name)
    {
        ConnectionId = connectionId;
        Name = name;
    }

    public Guid ConnectionId { get; private set; }

    public string Name { get; private set; } = string.Empty;
}

/// <summary>Metadata of a table or view. Never contains row data.</summary>
public sealed class DatabaseTable : Entity
{
    private DatabaseTable()
    {
    }

    public DatabaseTable(Guid connectionId, string schemaName, string name, DateTimeOffset seenAt)
    {
        ConnectionId = connectionId;
        SchemaName = schemaName;
        Name = name;
        FirstSeenAt = seenAt;
        LastSeenAt = seenAt;
    }

    public Guid ConnectionId { get; private set; }

    public string SchemaName { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public TableKind Kind { get; private set; }

    public string? Description { get; private set; }

    /// <summary>The engine's own estimate (statistics), not a count of customer data read by the platform.</summary>
    public long? EstimatedRowCount { get; private set; }

    public List<DatabaseColumn> Columns { get; private set; } = [];

    public List<DatabaseIndex> Indexes { get; private set; } = [];

    public int ColumnCount { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public void Update(TableKind kind, string? description, long? estimatedRowCount, IEnumerable<DatabaseColumn> columns, IEnumerable<DatabaseIndex> indexes, DateTimeOffset seenAt)
    {
        Kind = kind;
        Description = Truncate(description, 2000);
        EstimatedRowCount = estimatedRowCount is < 0 ? null : estimatedRowCount;
        Columns = columns.OrderBy(c => c.Ordinal).ToList();
        Indexes = indexes.ToList();
        ColumnCount = Columns.Count;
        LastSeenAt = seenAt;
    }

    internal static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value.Trim() : value[..length].Trim();
}

public sealed class DatabaseColumn
{
    private DatabaseColumn()
    {
    }

    public DatabaseColumn(string name, int ordinal, string dataType, int? maxLength, int? precision, int? scale, bool isNullable, bool isPrimaryKey, bool isIdentity, string? description)
    {
        Name = name;
        Ordinal = ordinal;
        DataType = dataType;
        MaxLength = maxLength;
        Precision = precision;
        Scale = scale;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        IsIdentity = isIdentity;
        Description = DatabaseTable.Truncate(description, 1000);
    }

    public string Name { get; private set; } = string.Empty;

    public int Ordinal { get; private set; }

    public string DataType { get; private set; } = string.Empty;

    /// <summary>Characters for text types; -1 means unbounded (e.g. varchar(max)).</summary>
    public int? MaxLength { get; private set; }

    public int? Precision { get; private set; }

    public int? Scale { get; private set; }

    public bool IsNullable { get; private set; }

    public bool IsPrimaryKey { get; private set; }

    public bool IsIdentity { get; private set; }

    public string? Description { get; private set; }
}

public sealed class DatabaseIndex
{
    private DatabaseIndex()
    {
    }

    public DatabaseIndex(string name, bool isUnique, bool isPrimaryKey, IEnumerable<string> columns)
    {
        Name = name;
        IsUnique = isUnique;
        IsPrimaryKey = isPrimaryKey;
        Columns = columns.ToList();
    }

    public string Name { get; private set; } = string.Empty;

    public bool IsUnique { get; private set; }

    public bool IsPrimaryKey { get; private set; }

    public List<string> Columns { get; private set; } = [];
}

/// <summary>A foreign-key relationship between two tables.</summary>
public sealed class DatabaseRelationship : Entity
{
    private DatabaseRelationship()
    {
    }

    public DatabaseRelationship(Guid connectionId, string name, string fromSchema, string fromTable, IEnumerable<string> fromColumns, string toSchema, string toTable, IEnumerable<string> toColumns)
    {
        ConnectionId = connectionId;
        Name = name;
        FromSchema = fromSchema;
        FromTable = fromTable;
        FromColumns = fromColumns.ToList();
        ToSchema = toSchema;
        ToTable = toTable;
        ToColumns = toColumns.ToList();
    }

    public Guid ConnectionId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string FromSchema { get; private set; } = string.Empty;

    public string FromTable { get; private set; } = string.Empty;

    public List<string> FromColumns { get; private set; } = [];

    public string ToSchema { get; private set; } = string.Empty;

    public string ToTable { get; private set; } = string.Empty;

    public List<string> ToColumns { get; private set; } = [];
}

/// <summary>
/// What a routine's definition does, determined by static analysis at discovery time. The definition text itself is
/// not stored (only a hash, to detect changes). <c>DefinitionAvailable=false</c> means the account cannot read it,
/// so the routine's behaviour is unknown and must not be assumed to be read-only.
/// </summary>
public sealed record RoutineBehavior(
    bool DefinitionAvailable,
    bool ModifiesData,
    bool DeletesData,
    bool ChangesSchema,
    bool UsesDynamicSql,
    bool CallsOtherRoutines)
{
    public static RoutineBehavior Unknown { get; } = new(false, false, false, false, false, false);

    /// <summary>
    /// Read-only only when the definition was readable and shows no writes, deletes, DDL, dynamic SQL or calls to other
    /// routines (whose behaviour cannot be judged from this definition).
    /// </summary>
    public bool IsReadOnly => DefinitionAvailable && !ModifiesData && !DeletesData && !ChangesSchema && !UsesDynamicSql && !CallsOtherRoutines;
}

/// <summary>
/// A stored procedure or function. Identity is stable across re-analysis (matched by schema, name and signature), so
/// AI tools generated from it keep their reference. Routines that disappear are flagged <see cref="IsMissing"/>.
/// </summary>
public sealed class StoredProcedure : Entity
{
    private StoredProcedure()
    {
    }

    public StoredProcedure(Guid connectionId, string schemaName, string name, string signature, DateTimeOffset seenAt)
    {
        ConnectionId = connectionId;
        SchemaName = schemaName;
        Name = name;
        Signature = signature;
        FirstSeenAt = seenAt;
        LastSeenAt = seenAt;
    }

    public Guid ConnectionId { get; private set; }

    public string SchemaName { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Distinguishes overloads (PostgreSQL); empty where names are unique.</summary>
    public string Signature { get; private set; } = string.Empty;

    public RoutineKind Kind { get; private set; }

    public string? Description { get; private set; }

    public string? ReturnType { get; private set; }

    /// <summary>Whether the connection's account holds EXECUTE on the routine.</summary>
    public bool CanExecute { get; private set; }

    public RoutineBehavior Behavior { get; private set; } = RoutineBehavior.Unknown;

    /// <summary>Provider hint about data access (e.g. MySQL "MODIFIES SQL DATA"), when declared.</summary>
    public string? DeclaredDataAccess { get; private set; }

    public string? DefinitionHash { get; private set; }

    public double? AverageDurationMs { get; private set; }

    public List<ProcedureParameter> Parameters { get; private set; } = [];

    public int ParameterCount { get; private set; }

    /// <summary>Columns of the (first) result set, when the engine can describe them.</summary>
    public List<ResultColumn> ResultColumns { get; private set; } = [];

    public bool IsMissing { get; private set; }

    public DateTimeOffset FirstSeenAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public void Update(
        RoutineKind kind,
        string? description,
        string? returnType,
        bool canExecute,
        RoutineBehavior behavior,
        string? declaredDataAccess,
        string? definitionHash,
        double? averageDurationMs,
        IEnumerable<ProcedureParameter> parameters,
        IEnumerable<ResultColumn> resultColumns,
        DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        Kind = kind;
        Description = DatabaseTable.Truncate(description, 2000);
        ReturnType = DatabaseTable.Truncate(returnType, 200);
        CanExecute = canExecute;
        Behavior = behavior;
        DeclaredDataAccess = DatabaseTable.Truncate(declaredDataAccess, 64);
        DefinitionHash = definitionHash;
        AverageDurationMs = averageDurationMs is < 0 ? null : averageDurationMs;
        Parameters = parameters.OrderBy(p => p.Ordinal).ToList();
        ResultColumns = resultColumns.OrderBy(c => c.Ordinal).ToList();
        ParameterCount = Parameters.Count;
        IsMissing = false;
        LastSeenAt = seenAt;
    }

    public void MarkMissing() => IsMissing = true;
}

public sealed class ProcedureParameter
{
    private ProcedureParameter()
    {
    }

    public ProcedureParameter(string name, int ordinal, string dataType, int? maxLength, int? precision, int? scale, ParameterDirection direction, bool hasDefault)
    {
        Name = name;
        Ordinal = ordinal;
        DataType = dataType;
        MaxLength = maxLength;
        Precision = precision;
        Scale = scale;
        Direction = direction;
        HasDefault = hasDefault;
    }

    public string Name { get; private set; } = string.Empty;

    public int Ordinal { get; private set; }

    public string DataType { get; private set; } = string.Empty;

    public int? MaxLength { get; private set; }

    public int? Precision { get; private set; }

    public int? Scale { get; private set; }

    public ParameterDirection Direction { get; private set; }

    /// <summary>The parameter may be omitted (has a default). Best effort for SQL Server, which does not expose T-SQL defaults.</summary>
    public bool HasDefault { get; private set; }
}

public sealed class ResultColumn
{
    private ResultColumn()
    {
    }

    public ResultColumn(string name, int ordinal, string dataType, bool isNullable)
    {
        Name = name;
        Ordinal = ordinal;
        DataType = dataType;
        IsNullable = isNullable;
    }

    public string Name { get; private set; } = string.Empty;

    public int Ordinal { get; private set; }

    public string DataType { get; private set; } = string.Empty;

    public bool IsNullable { get; private set; }
}

public enum AnalysisStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>One run of schema discovery for a connection.</summary>
public sealed class SchemaAnalysis : Entity
{
    public const int MaxWarnings = 20;

    private SchemaAnalysis()
    {
    }

    public SchemaAnalysis(Guid connectionId, Guid requestedByUserId, DateTimeOffset queuedAt)
    {
        ConnectionId = connectionId;
        RequestedByUserId = requestedByUserId;
        QueuedAt = queuedAt;
        Status = AnalysisStatus.Queued;
    }

    public Guid ConnectionId { get; private set; }

    public Guid RequestedByUserId { get; private set; }

    public Guid? JobId { get; private set; }

    public AnalysisStatus Status { get; private set; }

    public DateTimeOffset QueuedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public ConnectionFailureKind Failure { get; private set; }

    public int SchemaCount { get; private set; }

    public int TableCount { get; private set; }

    public int ViewCount { get; private set; }

    public int ColumnCount { get; private set; }

    public int RelationshipCount { get; private set; }

    public int ProcedureCount { get; private set; }

    public int NewProcedureCount { get; private set; }

    public int MissingProcedureCount { get; private set; }

    public List<string> Warnings { get; private set; } = [];

    public bool IsActive => Status is AnalysisStatus.Queued or AnalysisStatus.Running;

    public void AttachJob(Guid jobId) => JobId = jobId;

    /// <summary>Starts (or, after an interrupted worker, restarts) the analysis.</summary>
    public void Start(DateTimeOffset at)
    {
        if (!IsActive)
        {
            throw new DomainException("Only a queued or interrupted analysis can start.");
        }

        Status = AnalysisStatus.Running;
        StartedAt = at;
    }

    public void Complete(DateTimeOffset at, AnalysisCounts counts, IEnumerable<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(counts);
        Status = AnalysisStatus.Succeeded;
        CompletedAt = at;
        Failure = ConnectionFailureKind.None;
        (SchemaCount, TableCount, ViewCount, ColumnCount, RelationshipCount, ProcedureCount, NewProcedureCount, MissingProcedureCount) =
            (counts.Schemas, counts.Tables, counts.Views, counts.Columns, counts.Relationships, counts.Procedures, counts.NewProcedures, counts.MissingProcedures);
        Warnings = warnings.Take(MaxWarnings).ToList();
    }

    public void Fail(DateTimeOffset at, ConnectionFailureKind failure)
    {
        Status = AnalysisStatus.Failed;
        CompletedAt = at;
        Failure = failure == ConnectionFailureKind.None ? ConnectionFailureKind.Unknown : failure;
    }
}

public sealed record AnalysisCounts(int Schemas, int Tables, int Views, int Columns, int Relationships, int Procedures, int NewProcedures, int MissingProcedures);
