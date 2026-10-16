using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Jobs;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>
/// A unit of background work (schema analysis, embeddings, document processing, ...). Stored in the platform database
/// so it is enqueued atomically with the change that requested it, survives restarts and is claimed by one worker.
/// </summary>
public sealed class BackgroundJob : Entity
{
    public const int TypeMaxLength = 64;
    public const int ErrorMaxLength = 500;

    private BackgroundJob()
    {
    }

    public BackgroundJob(string type, string payload, int maxAttempts, DateTimeOffset createdAt)
    {
        Type = Text.Required(type, nameof(Type), TypeMaxLength);
        Payload = payload;
        MaxAttempts = Math.Clamp(maxAttempts, 1, 20);
        CreatedAt = createdAt;
        AvailableAt = createdAt;
        Status = JobStatus.Queued;
    }

    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public JobStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public int MaxAttempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Not picked up before this time (retry backoff).</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>While running, the claim expires at this time; an expired claim means the worker died.</summary>
    public DateTimeOffset? LockedUntil { get; private set; }

    public string? LockedBy { get; private set; }

    public string? LastError { get; private set; }

    public void Succeed(DateTimeOffset at)
    {
        Status = JobStatus.Succeeded;
        CompletedAt = at;
        LockedUntil = null;
        LastError = null;
    }

    /// <summary>Records a failure and either schedules a retry (exponential backoff) or gives up.</summary>
    public void Fail(DateTimeOffset at, string error)
    {
        LastError = error.Length <= ErrorMaxLength ? error : error[..ErrorMaxLength];
        LockedUntil = null;
        LockedBy = null;
        if (Attempts < MaxAttempts)
        {
            Status = JobStatus.Queued;
            AvailableAt = at + TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Attempts - 1), 3600));
        }
        else
        {
            Status = JobStatus.Failed;
            CompletedAt = at;
        }
    }
}
