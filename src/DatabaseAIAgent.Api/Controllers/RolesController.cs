using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Contracts;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Security.Users;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

[ApiController]
[Route("api/roles")]
public sealed class RolesController(RoleService roles, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.UsersView)]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> List(CancellationToken cancellationToken) =>
        Ok(await roles.ListAsync(currentUser.RequiredUserId, cancellationToken));

    /// <summary>The permission catalogue, for building the role editor.</summary>
    [HttpGet("permissions")]
    [RequirePermission(Permissions.UsersView)]
    public ActionResult<IReadOnlyList<string>> ListPermissions() => Ok(Permissions.All);

    [HttpPut("{roleName}/permissions")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<ActionResult<RoleDto>> UpdatePermissions(string roleName, UpdateRolePermissionsRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await roles.UpdatePermissionsAsync(currentUser.RequiredUserId, roleName, request.Permissions, cancellationToken));
}
