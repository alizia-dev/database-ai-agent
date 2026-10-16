using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Contracts;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Security.Users;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

[ApiController]
[Route("api/team")]
public sealed class TeamController(UserManagementService team, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet("members")]
    [RequirePermission(Permissions.UsersView)]
    public async Task<ActionResult<IReadOnlyList<TeamMemberDto>>> ListMembers(CancellationToken cancellationToken) =>
        Ok(await team.ListMembersAsync(cancellationToken));

    [HttpPut("members/{userId:guid}/role")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult<TeamMemberDto>> ChangeRole(Guid userId, ChangeRoleRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await team.ChangeRoleAsync(currentUser.RequiredUserId, userId, request.Role, cancellationToken));

    [HttpPost("members/{userId:guid}/deactivate")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult<TeamMemberDto>> Deactivate(Guid userId, CancellationToken cancellationToken) =>
        this.ToActionResult(await team.SetActiveAsync(currentUser.RequiredUserId, userId, active: false, cancellationToken));

    [HttpPost("members/{userId:guid}/reactivate")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult<TeamMemberDto>> Reactivate(Guid userId, CancellationToken cancellationToken) =>
        this.ToActionResult(await team.SetActiveAsync(currentUser.RequiredUserId, userId, active: true, cancellationToken));

    [HttpGet("invitations")]
    [RequirePermission(Permissions.UsersView)]
    public async Task<ActionResult<IReadOnlyList<InvitationDto>>> ListInvitations(CancellationToken cancellationToken) =>
        Ok(await team.ListInvitationsAsync(cancellationToken));

    [HttpPost("invitations")]
    [RequirePermission(Permissions.UsersManage)]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<InvitationDto>> Invite(InviteUserCommand command, CancellationToken cancellationToken)
    {
        var result = await team.InviteAsync(currentUser.RequiredUserId, command, cancellationToken);
        return result.IsSuccess
            ? StatusCode(StatusCodes.Status201Created, result.Value)
            : this.ToProblem(result.Error);
    }

    [HttpDelete("invitations/{invitationId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult> RevokeInvitation(Guid invitationId, CancellationToken cancellationToken) =>
        this.ToNoContent(await team.RevokeInvitationAsync(invitationId, cancellationToken));
}
