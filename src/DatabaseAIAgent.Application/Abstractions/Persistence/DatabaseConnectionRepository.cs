using DatabaseAIAgent.Domain.Databases;

namespace DatabaseAIAgent.Application.Abstractions.Persistence;

public interface IDatabaseConnectionRepository
{
    Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken);

    Task<DatabaseConnection?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    void Add(DatabaseConnection connection);

    void Remove(DatabaseConnection connection);
}
