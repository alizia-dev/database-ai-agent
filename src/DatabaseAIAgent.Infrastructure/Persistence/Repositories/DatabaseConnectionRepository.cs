using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Databases;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence.Repositories;

internal sealed class DatabaseConnectionRepository(PlatformDbContext db) : IDatabaseConnectionRepository
{
    public async Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken) =>
        await db.DatabaseConnections.OrderBy(c => c.Name).ToListAsync(cancellationToken);

    public Task<DatabaseConnection?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.DatabaseConnections.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        // Few connections per workspace: compare case-insensitively in memory (portable across providers).
        var names = await db.DatabaseConnections.Where(c => c.Id != exceptId).Select(c => c.Name).ToListAsync(cancellationToken);
        return names.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.DatabaseConnections.CountAsync(cancellationToken);

    public void Add(DatabaseConnection connection) => db.DatabaseConnections.Add(connection);

    public void Remove(DatabaseConnection connection) => db.DatabaseConnections.Remove(connection);
}
