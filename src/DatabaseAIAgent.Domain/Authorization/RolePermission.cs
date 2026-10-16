namespace DatabaseAIAgent.Domain.Authorization;

/// <summary>Grants one permission to one role.</summary>
public sealed class RolePermission
{
    private RolePermission()
    {
    }

    public RolePermission(Guid roleId, string permission)
    {
        if (!Permissions.IsKnown(permission))
        {
            throw new Common.DomainException($"Unknown permission '{permission}'.");
        }

        RoleId = roleId;
        Permission = permission;
    }

    public Guid RoleId { get; private set; }

    public string Permission { get; private set; } = string.Empty;
}
