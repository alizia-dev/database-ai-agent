using DatabaseAIAgent.Domain.Organizations;
using DatabaseAIAgent.Domain.Users;

namespace DatabaseAIAgent.Application.Abstractions.Persistence;

public interface IOrganizationRepository
{
    /// <summary>Returns the workspace singleton (seeded at startup).</summary>
    Task<Organization> GetAsync(CancellationToken cancellationToken);
}

public interface IUserActivityRepository
{
    /// <summary>
    /// Records a successful sign-in (last sign-in time, failed-attempt counter reset) with a single-row update.
    /// Deliberately bypasses Identity's optimistic concurrency stamp so simultaneous sign-ins never conflict.
    /// </summary>
    Task RecordSignInAsync(Guid userId, DateTimeOffset signedInAt, CancellationToken cancellationToken);
}

public interface IRefreshTokenRepository
{
    void Add(RefreshToken token);

    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> ListUnrevokedByFamilyAsync(Guid familyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RefreshToken>> ListUnrevokedByUserAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IUserInvitationRepository
{
    void Add(UserInvitation invitation);

    Task<UserInvitation?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<UserInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserInvitation>> ListPendingAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserInvitation>> ListPendingByEmailAsync(string normalizedEmail, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IRolePermissionRepository
{
    Task<IReadOnlyList<string>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken);

    /// <summary>Permissions granted through the user's role(s); empty for unknown or deactivated users.</summary>
    Task<IReadOnlyList<string>> GetPermissionsForActiveUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<string?> GetRoleForActiveUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the role's permission set (tracked; persisted on save).</summary>
    Task ReplaceAsync(Guid roleId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);
}
