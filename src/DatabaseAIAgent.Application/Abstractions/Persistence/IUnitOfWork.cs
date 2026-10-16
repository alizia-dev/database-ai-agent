namespace DatabaseAIAgent.Application.Abstractions.Persistence;

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Drops all pending (unsaved) changes, e.g. after a failed operation, so they are never persisted.</summary>
    void DiscardChanges();

    /// <summary>
    /// Runs <paramref name="operation"/> in a database transaction (retried as a unit on transient failures).
    /// The transaction commits only if the operation returns a successful result.
    /// </summary>
    Task<TResult> ExecuteInTransactionAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, Func<TResult, bool> shouldCommit, CancellationToken cancellationToken);
}
