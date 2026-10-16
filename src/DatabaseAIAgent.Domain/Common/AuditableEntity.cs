namespace DatabaseAIAgent.Domain.Common;

public abstract class AuditableEntity : Entity, IAuditableEntity
{
    protected AuditableEntity()
    {
    }

    protected AuditableEntity(Guid id)
        : base(id)
    {
    }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
