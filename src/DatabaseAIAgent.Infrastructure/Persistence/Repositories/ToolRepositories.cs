using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence.Repositories;

internal sealed class ToolRepository(PlatformDbContext db) : IToolRepository
{
    public async Task<IReadOnlyList<AiTool>> ListAsync(Guid? connectionId, CancellationToken cancellationToken) =>
        await db.AiTools.Where(t => connectionId == null || t.ConnectionId == connectionId).OrderBy(t => t.Name).ToListAsync(cancellationToken);

    public Task<AiTool?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.AiTools.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public Task<AiTool?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        db.AiTools.SingleOrDefaultAsync(t => t.Name == name, cancellationToken);

    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken) =>
        await db.AiTools.Select(t => t.Name).ToListAsync(cancellationToken);

    public Task<int> CountEnabledAsync(CancellationToken cancellationToken) =>
        db.AiTools.CountAsync(t => t.Status == ToolStatus.Enabled, cancellationToken);

    public void Add(AiTool tool) => db.AiTools.Add(tool);
}

internal sealed class ToolExecutionRepository(PlatformDbContext db) : IToolExecutionRepository
{
    private static readonly ToolExecutionStatus[] Executed = [ToolExecutionStatus.Succeeded, ToolExecutionStatus.Failed, ToolExecutionStatus.TimedOut];

    public void Add(ToolExecution execution) => db.ToolExecutions.Add(execution);

    public Task<int> CountExecutedSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        db.ToolExecutions.CountAsync(e => e.StartedAt >= since && Executed.Contains(e.Status), cancellationToken);

    public async Task<IReadOnlyList<ToolExecution>> ListRecentAsync(Guid toolId, int take, CancellationToken cancellationToken) =>
        await db.ToolExecutions.AsNoTracking().Where(e => e.ToolId == toolId).OrderByDescending(e => e.StartedAt).Take(take).ToListAsync(cancellationToken);
}
