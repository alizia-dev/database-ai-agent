using DatabaseAIAgent.Application.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence;

internal sealed class UnitOfWork(PlatformDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool> shouldCommit,
        CancellationToken cancellationToken)
    {
        // The retrying execution strategy requires user transactions to run inside it so the whole unit is retried.
        // Operations must therefore be safe to re-run; entities loaded before the transaction stay tracked.
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation(ct);
            if (shouldCommit(result))
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            else
            {
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
            }

            return result;
        }, cancellationToken);
    }
}
