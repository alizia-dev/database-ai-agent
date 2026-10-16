using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Databases;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Domain.Databases;
using DatabaseAIAgent.Domain.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using ParameterDirection = System.Data.ParameterDirection;
using RoutineParameterDirection = DatabaseAIAgent.Domain.Databases.ParameterDirection;

namespace DatabaseAIAgent.Database.Execution;

public sealed class ExecutionOptions
{
    public const string SectionName = "ToolExecution";

    /// <summary>Concurrent procedure calls per customer database; further calls fail fast as "busy".</summary>
    public int MaxConcurrentPerConnection { get; set; } = 5;

    /// <summary>Consecutive connection failures/timeouts that pause calls to a database.</summary>
    public int CircuitBreakerFailures { get; set; } = 5;

    public int CircuitBreakerSeconds { get; set; } = 30;
}

/// <summary>
/// Runs approved stored procedures. Only catalog-derived, quoted identifiers and typed parameters reach the database;
/// argument values are never concatenated into SQL. Read-only calls run inside a transaction that PostgreSQL and MySQL
/// enforce as read-only and that is always rolled back (SQL Server), so a misclassified procedure cannot persist a change.
/// </summary>
internal sealed class CustomerProcedureInvoker(
    ICustomerDatabaseConnector connector,
    ConnectionGates gates,
    IClock clock,
    ILogger<CustomerProcedureInvoker> logger) : ICustomerProcedureInvoker
{
    public async Task<ProcedureResult> InvokeAsync(ProcedureInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var gate = gates.For(invocation.ConnectionId);
        gate.ThrowIfOpen(clock.UtcNow);
        if (!await gate.Concurrency.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new ProcedureExecutionException(ProcedureFailure.TooBusy, "The database is handling the maximum number of requests. Please try again shortly.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(invocation.TimeoutSeconds));
        try
        {
            var result = await RunAsync(invocation, timeout.Token);
            gate.RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            gate.RecordFailure(clock.UtcNow);
            throw new ProcedureExecutionException(ProcedureFailure.Timeout, "The request took too long and was cancelled.");
        }
        catch (CustomerDatabaseUnavailableException ex)
        {
            gate.RecordFailure(clock.UtcNow);
            throw new ProcedureExecutionException(ProcedureFailure.Unavailable, "The database could not be reached.", ex);
        }
        catch (DbException ex)
        {
            var failure = Classify(ex);
            if (failure is ProcedureFailure.Timeout or ProcedureFailure.Unavailable)
            {
                gate.RecordFailure(clock.UtcNow);
            }

            // Driver messages can contain data values; log only the type and the engine's error code.
            logger.LogWarning("Procedure {Schema}.{Name} failed: {Failure} ({ErrorType} {ErrorCode})",
                invocation.Procedure.SchemaName, invocation.Procedure.Name, failure, ex.GetType().Name, ErrorCode(ex));
            throw new ProcedureExecutionException(failure, failure == ProcedureFailure.ReadOnlyViolation
                ? "The procedure tried to change data while running as read-only, so it was stopped."
                : "The database could not complete the request.", ex);
        }
        finally
        {
            gate.Concurrency.Release();
        }
    }

    private async Task<ProcedureResult> RunAsync(ProcedureInvocation invocation, CancellationToken cancellationToken)
    {
        await using var connection = await connector.OpenAsync(invocation.ConnectionId, cancellationToken);
        var provider = Provider(connection);
        DbTransaction? transaction = null;
        try
        {
            transaction = await BeginAsync(connection, provider, invocation.ReadOnly, cancellationToken);
            await using var command = BuildCommand(connection, provider, invocation);
            command.Transaction = transaction;
            command.CommandTimeout = invocation.TimeoutSeconds;

            var (columns, rows, truncated, bytes) = await ReadAsync(command, invocation, cancellationToken);
            var outputs = command.Parameters.Cast<DbParameter>()
                .Where(p => p.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                .ToDictionary(p => p.ParameterName.TrimStart('@'), p => Normalize(p.Value), StringComparer.Ordinal);

            if (transaction is not null)
            {
                if (invocation.ReadOnly)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                else
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            else if (invocation.ReadOnly && provider == DatabaseProviderKind.MySql)
            {
                await ExecuteAsync(connection, "ROLLBACK", cancellationToken);
            }

            return new ProcedureResult(columns, rows, outputs, truncated, bytes);
        }
        catch
        {
            if (transaction is not null)
            {
                await SafeRollbackAsync(transaction);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static async Task<DbTransaction?> BeginAsync(DbConnection connection, DatabaseProviderKind provider, bool readOnly, CancellationToken cancellationToken)
    {
        switch (provider)
        {
            case DatabaseProviderKind.PostgreSql:
                var pg = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                if (readOnly)
                {
                    await using var set = connection.CreateCommand();
                    set.Transaction = pg;
                    set.CommandText = "SET TRANSACTION READ ONLY";
                    await set.ExecuteNonQueryAsync(cancellationToken);
                }

                return pg;
            case DatabaseProviderKind.MySql when readOnly:
                // MySqlConnector's transaction API has no read-only flag; the server enforces START TRANSACTION READ ONLY.
                await ExecuteAsync(connection, "START TRANSACTION READ ONLY", cancellationToken);
                return null;
            default:
                return await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        }
    }

    internal static DbCommand BuildCommand(DbConnection connection, DatabaseProviderKind provider, ProcedureInvocation invocation)
    {
        var procedure = invocation.Procedure;
        var command = connection.CreateCommand();
        var supplied = invocation.Arguments.ToDictionary(a => a.SourceName, StringComparer.OrdinalIgnoreCase);
        var inputs = procedure.Parameters.Where(p => p.Direction != RoutineParameterDirection.Out).OrderBy(p => p.Ordinal).ToList();
        var name = $"{Quote(provider, procedure.SchemaName)}.{Quote(provider, procedure.Name)}";

        if (procedure.Kind == RoutineKind.Procedure && provider != DatabaseProviderKind.PostgreSql)
        {
            // SQL Server / MySQL procedures: native stored-procedure call; omitted optional parameters use their defaults.
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = name;
            foreach (var p in procedure.Parameters.OrderBy(p => p.Ordinal))
            {
                if (p.Direction == RoutineParameterDirection.Out)
                {
                    command.Parameters.Add(Parameter(command, provider, p.Name, null, ToolParameterType.String, ParameterDirection.Output));
                }
                else if (supplied.TryGetValue(p.Name, out var arg))
                {
                    command.Parameters.Add(Parameter(command, provider, p.Name, Narrow(p.DataType, arg.Value), arg.Type,
                        p.Direction == RoutineParameterDirection.InOut ? ParameterDirection.InputOutput : ParameterDirection.Input));
                }
                else if (provider == DatabaseProviderKind.MySql)
                {
                    command.Parameters.Add(Parameter(command, provider, p.Name, null, ToolParameterType.String, ParameterDirection.Input));
                }
            }

            return command;
        }

        var argumentList = new List<string>();
        var index = 0;
        foreach (var p in inputs)
        {
            if (supplied.TryGetValue(p.Name, out var arg))
            {
                var placeholder = $"@p{index++}";
                command.Parameters.Add(Parameter(command, provider, placeholder, Narrow(p.DataType, arg.Value), arg.Type, ParameterDirection.Input));
                argumentList.Add(provider == DatabaseProviderKind.PostgreSql && !p.Name.StartsWith("arg", StringComparison.Ordinal)
                    ? $"{Quote(provider, p.Name)} => {placeholder}"
                    : placeholder);
            }
            else if (provider == DatabaseProviderKind.SqlServer)
            {
                argumentList.Add("DEFAULT"); // T-SQL functions require DEFAULT for omitted parameters
            }
            else if (provider == DatabaseProviderKind.MySql)
            {
                argumentList.Add("NULL");
            }
            // PostgreSQL named notation: omitted parameters with defaults are simply left out.
        }

        var args = string.Join(", ", argumentList);
        command.CommandText = (provider, procedure.Kind) switch
        {
            (DatabaseProviderKind.PostgreSql, RoutineKind.Procedure) => $"CALL {name}({args})",
            (_, RoutineKind.TableFunction) => $"SELECT * FROM {name}({args})",
            _ => $"SELECT {name}({args}) AS {Quote(provider, "result")}",
        };
        return command;
    }

    /// <summary>
    /// Integers arrive as 64-bit; send them at the declared width, because PostgreSQL resolves functions by argument
    /// type and does not implicitly narrow bigint to integer. (Range was already validated against the declared type.)
    /// </summary>
    internal static object? Narrow(string dataType, object? value)
    {
        if (value is not long number)
        {
            return value;
        }

        var type = dataType.Split('(')[0].Trim().ToLowerInvariant();
        // Each arm is boxed separately: a switch expression would otherwise widen every arm back to long.
        return type switch
        {
            "int" or "integer" or "int4" or "mediumint" => (object)checked((int)number),
            "smallint" or "int2" => (object)checked((short)number),
            "tinyint" => (object)checked((byte)number),
            _ => number,
        };
    }

    private static DbParameter Parameter(DbCommand command, DatabaseProviderKind provider, string name, object? value, ToolParameterType type, ParameterDirection direction)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name.StartsWith('@') ? name : "@" + name;
        parameter.Direction = direction;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            DateTimeOffset dto when provider == DatabaseProviderKind.PostgreSql => dto.UtcDateTime,
            DateTimeOffset dto when provider == DatabaseProviderKind.MySql => dto.UtcDateTime,
            _ => value,
        };
        if (direction == ParameterDirection.Output)
        {
            parameter.DbType = DbType.String;
            parameter.Size = 4000;
        }
        else if (value is null)
        {
            parameter.DbType = type switch
            {
                ToolParameterType.Integer => DbType.Int64,
                ToolParameterType.Number => DbType.Decimal,
                ToolParameterType.Boolean => DbType.Boolean,
                ToolParameterType.Uuid => DbType.Guid,
                _ => DbType.String,
            };
        }

        return parameter;
    }

    private static async Task<(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, long Bytes)> ReadAsync(
        DbCommand command, ProcedureInvocation invocation, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount == 0)
        {
            return ([], [], false, 0);
        }

        var columns = Enumerable.Range(0, reader.FieldCount).Select(i => string.IsNullOrEmpty(reader.GetName(i)) ? $"column{i + 1}" : reader.GetName(i)).ToList();
        var rows = new List<IReadOnlyList<object?>>();
        long bytes = 0;
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= invocation.MaxRows)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
                bytes += EstimateBytes(row[i]);
            }

            if (bytes > invocation.MaxResultBytes)
            {
                truncated = true;
                break;
            }

            rows.Add(row);
        }

        return (columns, rows, truncated, bytes);
    }

    /// <summary>JSON-friendly values; binary and engine-specific types are summarized rather than returned raw.</summary>
    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string or bool or byte or short or int or long or float or double or decimal or Guid => value,
        DateTime dt => dt,
        DateTimeOffset dto => dto,
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
        byte[] b => $"[binary data, {b.Length} bytes]",
        sbyte or ushort or uint or ulong => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => Truncate(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), 2000),
    };

    private static long EstimateBytes(object? value) => value switch
    {
        null => 4,
        string s => Encoding.UTF8.GetByteCount(s) + 2,
        _ => 16,
    };

    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    internal static string Quote(DatabaseProviderKind provider, string identifier) => provider switch
    {
        DatabaseProviderKind.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
        DatabaseProviderKind.MySql => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
        _ => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
    };

    private static DatabaseProviderKind Provider(DbConnection connection) => connection switch
    {
        SqlConnection => DatabaseProviderKind.SqlServer,
        NpgsqlConnection => DatabaseProviderKind.PostgreSql,
        MySqlConnection => DatabaseProviderKind.MySql,
        _ => DatabaseProviderKind.SqlServer,
    };

    private static ProcedureFailure Classify(DbException exception) => exception switch
    {
        SqlException { Number: -2 } => ProcedureFailure.Timeout,
        SqlException { Class: >= 20 } => ProcedureFailure.Unavailable,
        PostgresException { SqlState: "25006" } => ProcedureFailure.ReadOnlyViolation, // read_only_sql_transaction
        PostgresException { SqlState: "57014" } => ProcedureFailure.Timeout, // query_canceled
        PostgresException { SqlState: "0A000" } => ProcedureFailure.ReadOnlyViolation, // e.g. "INSERT is not allowed in a non-volatile function"
        MySqlException { Number: 1792 } => ProcedureFailure.ReadOnlyViolation, // cannot execute in a READ ONLY transaction
        MySqlException { Number: 3024 } => ProcedureFailure.Timeout,
        _ => ProcedureFailure.DatabaseError,
    };

    private static string ErrorCode(DbException exception) => exception switch
    {
        SqlException sql => sql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PostgresException pg => pg.SqlState,
        MySqlException mysql => mysql.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => exception.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SafeRollbackAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // The connection may already be broken; disposing it ends the transaction.
        }
    }
}

/// <summary>Per-connection concurrency limit and circuit breaker (process-wide).</summary>
internal sealed class ConnectionGates(IOptions<ExecutionOptions> options)
{
    private readonly ConcurrentDictionary<Guid, ConnectionGate> _gates = new();

    public ConnectionGate For(Guid connectionId) =>
        _gates.GetOrAdd(connectionId, _ => new ConnectionGate(options.Value));
}

internal sealed class ConnectionGate(ExecutionOptions options)
{
    private readonly Lock _lock = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public SemaphoreSlim Concurrency { get; } = new(Math.Max(1, options.MaxConcurrentPerConnection));

    public void ThrowIfOpen(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_openUntil is { } until && now < until)
            {
                throw new ProcedureExecutionException(ProcedureFailure.Unavailable, "Requests to this database are paused briefly after repeated failures. Please try again shortly.");
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    public void RecordFailure(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (++_consecutiveFailures >= options.CircuitBreakerFailures)
            {
                _openUntil = now.AddSeconds(options.CircuitBreakerSeconds);
                _consecutiveFailures = 0;
            }
        }
    }
}
