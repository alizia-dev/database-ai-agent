namespace DatabaseAIAgent.Application.Abstractions.Persistence;

/// <summary>
/// Reference data a module needs at startup (e.g. billing plans). Seeders run after migrations, in <see cref="Order"/>,
/// and must be idempotent: insert what is missing, never overwrite administrator changes.
/// </summary>
public interface IDataSeeder
{
    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}
