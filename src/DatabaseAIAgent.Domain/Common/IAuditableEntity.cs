namespace DatabaseAIAgent.Domain.Common;

/// <summary>Entities whose creation/modification timestamps are maintained by the persistence layer.</summary>
public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset? UpdatedAt { get; set; }
}
