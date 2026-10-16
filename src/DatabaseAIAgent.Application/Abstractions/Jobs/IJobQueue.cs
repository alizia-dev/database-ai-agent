namespace DatabaseAIAgent.Application.Abstractions.Jobs;

/// <summary>
/// Queue for background work. The default implementation stores jobs in the platform database: an enqueued job is
/// saved together with the caller's other changes (<see cref="Persistence.IUnitOfWork.SaveChangesAsync"/>), so a job
/// is never lost or orphaned. The abstraction allows a broker (Redis, RabbitMQ, Azure Service Bus, SQS) later.
/// </summary>
public interface IJobQueue
{
    /// <summary>Adds a job; it becomes visible to workers when the caller saves.</summary>
    Guid Enqueue<TPayload>(string type, TPayload payload, int maxAttempts = 3);
}

/// <summary>Processes one job type. Handlers run in their own DI scope, in the Workers host.</summary>
public interface IJobHandler
{
    string JobType { get; }

    /// <summary>How long a claim lasts before another worker may assume this one died.</summary>
    TimeSpan LockDuration => TimeSpan.FromMinutes(10);

    Task HandleAsync(JobContext context, CancellationToken cancellationToken);
}

public sealed record JobContext(Guid JobId, string Payload, int Attempt, int MaxAttempts)
{
    public bool IsLastAttempt => Attempt >= MaxAttempts;

    public T Read<T>() => System.Text.Json.JsonSerializer.Deserialize<T>(Payload, JsonOptions)
        ?? throw new InvalidOperationException($"Job {JobId} has an empty payload.");

    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
}

/// <summary>Processes queued jobs. Exposed so hosts (and tests) can drain the queue deterministically.</summary>
public interface IJobProcessor
{
    /// <summary>Claims and runs at most one due job. Returns false when nothing was due.</summary>
    Task<bool> ProcessNextAsync(CancellationToken cancellationToken);
}
