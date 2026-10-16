using System.Collections.Concurrent;
using DatabaseAIAgent.Application.Abstractions.Tools;

namespace DatabaseAIAgent.Api.Tests.Tools;

/// <summary>
/// Replaces only the network edge of tool execution: records each invocation the real pipeline produces and returns
/// a canned result (or failure) configured per procedure name.
/// </summary>
public sealed class FakeProcedureInvoker : ICustomerProcedureInvoker
{
    public ConcurrentQueue<ProcedureInvocation> Invocations { get; } = new();

    public ConcurrentDictionary<string, ProcedureFailure> Failures { get; } = new(StringComparer.Ordinal);

    public Task<ProcedureResult> InvokeAsync(ProcedureInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        Invocations.Enqueue(invocation);
        if (Failures.TryGetValue(invocation.Procedure.Name, out var failure))
        {
            throw new ProcedureExecutionException(failure, failure == ProcedureFailure.Timeout
                ? "The request took too long and was cancelled."
                : "The database could not complete the request.");
        }

        return Task.FromResult(new ProcedureResult(
            ["id", "name"],
            [[1, "Contoso"], [2, "Fabrikam"]],
            new Dictionary<string, object?>(),
            false,
            64));
    }

    public IReadOnlyList<ProcedureInvocation> For(string procedureName) =>
        Invocations.Where(i => i.Procedure.Name == procedureName).ToList();
}
