using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Common;
using DatabaseAIAgent.Domain.Organizations;

namespace DatabaseAIAgent.Application.Workspace;

public sealed record WorkspaceSettingsDto(
    string Name,
    string? Industry,
    string AssistantName,
    string? BusinessDescription,
    string TimeZone,
    string Currency,
    string DateFormat,
    ResponseStyle ResponseStyle,
    RetentionPolicyDto Retention,
    DateTimeOffset? UpdatedAt);

public sealed record RetentionPolicyDto(int? ConversationDays, int? AuditLogDays, int? UsageRecordDays, int? KnowledgeDocumentDays);

public sealed record UpdateWorkspaceSettingsRequest(
    string Name,
    string? Industry,
    string AssistantName,
    string? BusinessDescription,
    string TimeZone,
    string Currency,
    string DateFormat,
    ResponseStyle ResponseStyle,
    RetentionPolicyDto Retention);

public sealed class WorkspaceSettingsService(IOrganizationRepository organizations, IUnitOfWork unitOfWork)
{
    public async Task<WorkspaceSettingsDto> GetAsync(CancellationToken cancellationToken) =>
        ToDto(await organizations.GetAsync(cancellationToken));

    public async Task<Result<WorkspaceSettingsDto>> UpdateAsync(UpdateWorkspaceSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = await organizations.GetAsync(cancellationToken);
        var retention = request.Retention ?? new RetentionPolicyDto(null, null, null, null);

        try
        {
            organization.Update(
                request.Name,
                request.Industry,
                request.AssistantName,
                request.BusinessDescription,
                request.TimeZone,
                request.Currency,
                request.DateFormat,
                request.ResponseStyle,
                new RetentionPolicy(retention.ConversationDays, retention.AuditLogDays, retention.UsageRecordDays, retention.KnowledgeDocumentDays));
        }
        catch (DomainException ex)
        {
            return Error.Validation("workspace.invalid_settings", ex.Message);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(organization);
    }

    private static WorkspaceSettingsDto ToDto(Organization o) => new(
        o.Name,
        o.Industry,
        o.AssistantName,
        o.BusinessDescription,
        o.TimeZone,
        o.Currency,
        o.DateFormat,
        o.ResponseStyle,
        new RetentionPolicyDto(o.Retention.ConversationDays, o.Retention.AuditLogDays, o.Retention.UsageRecordDays, o.Retention.KnowledgeDocumentDays),
        o.UpdatedAt);
}
