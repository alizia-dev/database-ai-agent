using DatabaseAIAgent.Domain.Common;

namespace DatabaseAIAgent.Domain.Organizations;

/// <summary>Retention periods in days. <c>null</c> means "keep indefinitely".</summary>
public sealed record RetentionPolicy(int? ConversationDays, int? AuditLogDays, int? UsageRecordDays, int? KnowledgeDocumentDays)
{
    public const int MinimumDays = 1;
    public const int MaximumDays = 3650;

    public static RetentionPolicy Default { get; } = new(ConversationDays: 365, AuditLogDays: 730, UsageRecordDays: 730, KnowledgeDocumentDays: null);

    public void Validate()
    {
        Check(ConversationDays, nameof(ConversationDays));
        Check(AuditLogDays, nameof(AuditLogDays));
        Check(UsageRecordDays, nameof(UsageRecordDays));
        Check(KnowledgeDocumentDays, nameof(KnowledgeDocumentDays));
    }

    private static void Check(int? days, string name)
    {
        if (days is < MinimumDays or > MaximumDays)
        {
            throw new DomainException($"{name} must be between {MinimumDays} and {MaximumDays} days, or empty to keep indefinitely.");
        }
    }
}
