namespace DatabaseAIAgent.Application.Abstractions.Authorization;

/// <summary>
/// Resolves a user's effective permissions from their role. This is the single source of truth for
/// authorization decisions; tokens never carry permissions.
/// </summary>
public interface IPermissionService
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken);

    async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken) =>
        (await GetPermissionsAsync(userId, cancellationToken)).Contains(permission);

    /// <summary>Drops cached permissions for one user (e.g. after a role change).</summary>
    void Invalidate(Guid userId);

    /// <summary>Drops all cached permissions (e.g. after a role's permission set changes).</summary>
    void InvalidateAll();
}
