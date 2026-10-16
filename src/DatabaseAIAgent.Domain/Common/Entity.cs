namespace DatabaseAIAgent.Domain.Common;

/// <summary>Base type for all platform entities. Identity is a time-ordered GUID assigned at creation.</summary>
public abstract class Entity
{
    protected Entity()
    {
        Id = Guid.CreateVersion7();
    }

    protected Entity(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; private set; }
}
