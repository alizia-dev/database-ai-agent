using DatabaseAIAgent.Domain.Tools;

namespace DatabaseAIAgent.Application.Abstractions.Persistence;

public interface IToolRepository
{
    Task<IReadOnlyList<AiTool>> ListAsync(Guid? connectionId, CancellationToken cancellationToken);

    Task<AiTool?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<AiTool?> FindByNameAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken);

    Task<int> CountEnabledAsync(CancellationToken cancellationToken);

    void Add(AiTool tool);
}

public interface IToolExecutionRepository
{
    void Add(ToolExecution execution);

    /// <summary>Executions that reached the database since <paramref name="since"/> (counted against the plan's monthly limit).</summary>
    Task<int> CountExecutedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    Task<IReadOnlyList<ToolExecution>> ListRecentAsync(Guid toolId, int take, CancellationToken cancellationToken);
}
