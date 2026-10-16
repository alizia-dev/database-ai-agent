using System.Text.Json;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Jobs;
using DatabaseAIAgent.Domain.Jobs;
using DatabaseAIAgent.Infrastructure.Persistence;

namespace DatabaseAIAgent.Infrastructure.Jobs;

/// <summary>Enqueues jobs into the platform database as part of the caller's unit of work (outbox semantics).</summary>
internal sealed class DatabaseJobQueue(PlatformDbContext db, IClock clock) : IJobQueue
{
    public Guid Enqueue<TPayload>(string type, TPayload payload, int maxAttempts = 3)
    {
        var job = new BackgroundJob(type, JsonSerializer.Serialize(payload, JobContext.JsonOptions), maxAttempts, clock.UtcNow);
        db.BackgroundJobs.Add(job);
        return job.Id;
    }
}
