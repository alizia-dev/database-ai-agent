using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Users;
using Microsoft.AspNetCore.Identity;

namespace DatabaseAIAgent.Security.Users;

/// <summary>Shared user/role lookups. Every user holds exactly one system role.</summary>
public sealed class UserDirectory(UserManager<User> users)
{
    public async Task<string> GetRoleAsync(User user)
    {
        var roles = await users.GetRolesAsync(user);
        // Defensive: should never happen, but an unexpected multi-role user is treated as the least privileged role held.
        return roles.Count == 1 ? roles[0] : roles.OrderByDescending(Rank).FirstOrDefault() ?? SystemRoles.Viewer;
    }

    public async Task<int> CountActiveOwnersAsync() =>
        (await users.GetUsersInRoleAsync(SystemRoles.Owner)).Count(u => u.IsActive);

    public async Task<bool> OwnerExistsAsync() =>
        (await users.GetUsersInRoleAsync(SystemRoles.Owner)).Count > 0;

    /// <summary>0 = most privileged (Owner).</summary>
    public static int Rank(string role)
    {
        for (var i = 0; i < SystemRoles.All.Count; i++)
        {
            if (SystemRoles.All[i] == role)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    /// <summary>An actor may grant/manage only roles at or below their own privilege level.</summary>
    public static bool CanAssign(string actorRole, string targetRole) => Rank(targetRole) >= Rank(actorRole);

    /// <summary>For Identity operations that are not expected to fail (e.g. concurrency); surfaces as a generic 500.</summary>
    public static void EnsureSucceeded(IdentityResult result, string operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Identity operation '{operation}' failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");
        }
    }

    public static IReadOnlyDictionary<string, string[]> ToFieldErrors(IEnumerable<IdentityError> errors, string field) =>
        new Dictionary<string, string[]> { [field] = errors.Select(e => e.Description).ToArray() };
}
