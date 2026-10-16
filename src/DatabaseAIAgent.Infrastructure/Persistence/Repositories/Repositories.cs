using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Organizations;
using DatabaseAIAgent.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAIAgent.Infrastructure.Persistence.Repositories;

internal sealed class OrganizationRepository(PlatformDbContext db) : IOrganizationRepository
{
    public async Task<Organization> GetAsync(CancellationToken cancellationToken) =>
        await db.Organizations.SingleOrDefaultAsync(o => o.Id == Organization.SingletonId, cancellationToken)
        ?? throw new InvalidOperationException("Workspace settings have not been seeded.");
}

internal sealed class UserActivityRepository(PlatformDbContext db) : IUserActivityRepository
{
    public Task RecordSignInAsync(Guid userId, DateTimeOffset signedInAt, CancellationToken cancellationToken) =>
        db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(
            s => s.SetProperty(u => u.LastLoginAt, signedInAt).SetProperty(u => u.AccessFailedCount, 0),
            cancellationToken);
}

internal sealed class RefreshTokenRepository(PlatformDbContext db) : IRefreshTokenRepository
{
    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListUnrevokedByFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RefreshToken>> ListUnrevokedByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(cancellationToken);
}

internal sealed class UserInvitationRepository(PlatformDbContext db) : IUserInvitationRepository
{
    public void Add(UserInvitation invitation) => db.UserInvitations.Add(invitation);

    public Task<UserInvitation?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.UserInvitations.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<UserInvitation?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.UserInvitations.SingleOrDefaultAsync(i => i.TokenHash == tokenHash, cancellationToken);

    public async Task<IReadOnlyList<UserInvitation>> ListPendingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Expiry is compared in memory: pending invitations are few, and this keeps the query provider-neutral.
        var open = await db.UserInvitations.Where(i => i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(cancellationToken);
        return open.Where(i => i.IsPending(now)).OrderBy(i => i.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<UserInvitation>> ListPendingByEmailAsync(string normalizedEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = await db.UserInvitations
            .Where(i => i.NormalizedEmail == normalizedEmail && i.AcceptedAt == null && i.RevokedAt == null)
            .ToListAsync(cancellationToken);
        return open.Where(i => i.IsPending(now)).ToList();
    }
}

internal sealed class RolePermissionRepository(PlatformDbContext db) : IRolePermissionRepository
{
    public async Task<IReadOnlyList<string>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken) =>
        await db.RolePermissions.Where(rp => rp.RoleId == roleId).Select(rp => rp.Permission).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> GetPermissionsForActiveUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await (from user in db.Users
               where user.Id == userId && user.IsActive
               join userRole in db.UserRoles on user.Id equals userRole.UserId
               join rolePermission in db.RolePermissions on userRole.RoleId equals rolePermission.RoleId
               select rolePermission.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await db.RolePermissions.AsNoTracking().ToListAsync(cancellationToken);
        return rows.GroupBy(rp => rp.RoleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(rp => rp.Permission).ToList());
    }

    public async Task ReplaceAsync(Guid roleId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        var existing = await db.RolePermissions.Where(rp => rp.RoleId == roleId).ToListAsync(cancellationToken);
        var desired = permissions.ToHashSet(StringComparer.Ordinal);

        db.RolePermissions.RemoveRange(existing.Where(rp => !desired.Contains(rp.Permission)));
        var current = existing.Select(rp => rp.Permission).ToHashSet(StringComparer.Ordinal);
        db.RolePermissions.AddRange(desired.Where(p => !current.Contains(p)).Select(p => new RolePermission(roleId, p)));
    }
}
