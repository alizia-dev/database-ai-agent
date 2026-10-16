using DatabaseAIAgent.Application.Abstractions.Authorization;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace DatabaseAIAgent.Security.Users;

public sealed record RoleDto(string Name, IReadOnlyList<string> Permissions, int MemberCount, bool Editable);

/// <summary>
/// Role permission configuration. Owner always holds every permission. Admin's permissions can only be changed by
/// an Owner. An actor can never grant a permission they do not hold themselves.
/// </summary>
public sealed class RoleService(
    RoleManager<Role> roles,
    UserManager<User> users,
    UserDirectory directory,
    IRolePermissionRepository rolePermissions,
    IPermissionService permissions,
    IUnitOfWork unitOfWork,
    ILogger<RoleService> logger)
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(Guid actorId, CancellationToken cancellationToken)
    {
        var actorRole = await ActorRoleAsync(actorId);
        var granted = await rolePermissions.GetAllAsync(cancellationToken);
        var result = new List<RoleDto>();
        foreach (var name in SystemRoles.All)
        {
            var role = await roles.FindByNameAsync(name) ?? throw new InvalidOperationException($"System role {name} is not seeded.");
            var rolePerms = granted.TryGetValue(role.Id, out var list) ? list : [];
            var memberCount = (await users.GetUsersInRoleAsync(name)).Count;
            result.Add(new RoleDto(name, Permissions.All.Where(rolePerms.Contains).ToList(), memberCount, CanEdit(actorRole, name)));
        }

        return result;
    }

    public async Task<Result<RoleDto>> UpdatePermissionsAsync(Guid actorId, string roleName, IReadOnlyCollection<string> requested, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var role = SystemRoles.IsKnown(roleName) ? await roles.FindByNameAsync(roleName) : null;
        if (role is null)
        {
            return Error.NotFound("roles.not_found", "That role was not found.");
        }

        var unknown = requested.Where(p => !Permissions.IsKnown(p)).ToArray();
        if (unknown.Length > 0)
        {
            return Error.Validation("roles.unknown_permission", "One or more permissions are not recognized.", new Dictionary<string, string[]> { ["permissions"] = unknown });
        }

        var actorRole = await ActorRoleAsync(actorId);
        if (!CanEdit(actorRole, roleName))
        {
            return Error.Forbidden("roles.not_editable", roleName == SystemRoles.Owner
                ? "The Owner role always has every permission."
                : "You cannot change the permissions of this role.");
        }

        var actorPermissions = await permissions.GetPermissionsAsync(actorId, cancellationToken);
        var current = await rolePermissions.GetPermissionsAsync(role.Id, cancellationToken);
        var added = requested.Except(current, StringComparer.Ordinal).ToArray();
        if (added.Any(p => !actorPermissions.Contains(p)))
        {
            return Error.Forbidden("roles.cannot_grant", "You cannot grant a permission you do not have.");
        }

        var desired = requested.Distinct(StringComparer.Ordinal).ToArray();
        await rolePermissions.ReplaceAsync(role.Id, desired, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        permissions.InvalidateAll();
        logger.LogInformation("User {ActorId} updated permissions of role {Role}", actorId, roleName);

        var memberCount = (await users.GetUsersInRoleAsync(roleName)).Count;
        return new RoleDto(roleName, Permissions.All.Where(desired.Contains).ToList(), memberCount, Editable: true);
    }

    private async Task<string> ActorRoleAsync(Guid actorId)
    {
        var actor = await users.FindByIdAsync(actorId.ToString()) ?? throw new InvalidOperationException("Authenticated actor not found.");
        return await directory.GetRoleAsync(actor);
    }

    private static bool CanEdit(string actorRole, string targetRole) =>
        targetRole != SystemRoles.Owner
        && (targetRole != SystemRoles.Admin || actorRole == SystemRoles.Owner)
        && UserDirectory.CanAssign(actorRole, targetRole);
}
