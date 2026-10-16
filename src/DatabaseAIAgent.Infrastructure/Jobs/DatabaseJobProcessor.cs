using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Domain.Jobs;
using DatabaseAIAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Infrastructure.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>How often an idle worker checks for new jobs.</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>Run job processing inside the API host too (local development without a separate worker).</summary>
    public bool ProcessInApi { get; set; }
}

/// <summary>
/// Claims due jobs with a conditional update (only one worker can move a job from Queued to Running), runs the
/// matching <see cref="IJobHandler"/> in its own scope, and records success or a retry with backoff. Jobs whose claim
/// expired (the worker died) are returned to the queue.
/// </summary>
internal sealed class DatabaseJobProcessor(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<DatabaseJobProcessor> logger) : IJobProcessor
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>().ToDictionary(h => h.JobType, StringComparer.Ordinal);
        var now = clock.UtcNow;

        await RecoverExpiredClaimsAsync(db, now, cancellationToken);

        var candidates = await db.BackgroundJobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Queued && j.AvailableAt <= now)
            .OrderBy(j => j.AvailableAt)
            .Select(j => new { j.Id, j.Type })
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (!handlers.TryGetValue(candidate.Type, out var handler))
            {
                continue;
            }

            var lockedUntil = now + handler.LockDuration;
            var claimed = await db.BackgroundJobs
                .Where(j => j.Id == candidate.Id && j.Status == JobStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Running)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.StartedAt, now)
                    .SetProperty(j => j.LockedUntil, lockedUntil)
                    .SetProperty(j => j.LockedBy, _workerId), cancellationToken);

            if (claimed == 1)
            {
                await RunAsync(candidate.Id, handler.JobType, cancellationToken);
                return true;
            }
        }

        return false;
    }

    private async Task RunAsync(Guid jobId, string jobType, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().Single(h => h.JobType == jobType);
        var job = await db.BackgroundJobs.SingleAsync(j => j.Id == jobId, cancellationToken);

        try
        {
            await handler.HandleAsync(new JobContext(job.Id, job.Payload, job.Attempts, job.MaxAttempts), cancellationToken);
            job = await ReloadAsync(db, jobId, cancellationToken);
            job.Succeed(clock.UtcNow);
            logger.LogInformation("Job {JobId} ({JobType}) succeeded", jobId, jobType);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Job {JobId} ({JobType}) failed on attempt {Attempt}", jobId, jobType, job.Attempts);
            job = await ReloadAsync(db, jobId, CancellationToken.None);
            job.Fail(clock.UtcNow, ex.GetType().Name);
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    // Handlers may have saved or discarded their own changes; start from a clean tracker and the current job row.
    private static async Task<BackgroundJob> ReloadAsync(PlatformDbContext db, Guid jobId, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        return await db.BackgroundJobs.SingleAsync(j => j.Id == jobId, cancellationToken);
    }

    private async Task RecoverExpiredClaimsAsync(PlatformDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recovered = await db.BackgroundJobs
            .Where(j => j.Status == JobStatus.Running && j.LockedUntil < now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LockedBy, (string?)null)
                .SetProperty(j => j.LastError, "Interrupted (worker stopped before completion)."), cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning("Returned {Count} interrupted job(s) to the queue", recovered);
        }
    }
}

/// <summary>Polls the job queue while the host runs.</summary>
internal sealed class JobWorkerService(IJobProcessor processor, IOptions<JobOptions> options, ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 1, 60));
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                processed = await processor.ProcessNextAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Job polling failed");
                processed = false;
            }

            if (!processed)
            {
                await Task.Delay(idleDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }
}
