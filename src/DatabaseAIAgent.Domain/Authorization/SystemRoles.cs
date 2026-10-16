namespace DatabaseAIAgent.Domain.Authorization;

/// <summary>
/// The fixed set of workspace roles. Each user holds exactly one. Role <em>permissions</em> are configurable,
/// except Owner, which always holds every permission.
/// </summary>
public static class SystemRoles
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Analyst = "Analyst";
    public const string Viewer = "Viewer";

    /// <summary>Ordered from most to least privileged.</summary>
    public static IReadOnlyList<string> All { get; } = [Owner, Admin, Manager, Analyst, Viewer];

    public static bool IsKnown(string role) => All.Contains(role, StringComparer.Ordinal);

    /// <summary>Default permissions seeded for a new deployment. Administrators can change all but Owner.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultPermissions { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Owner] = Permissions.All,
            [Admin] = Permissions.All.Where(p => p != Permissions.BillingManage).ToArray(),
            [Manager] =
            [
                Permissions.DatabaseView, Permissions.ToolsView, Permissions.ToolsExecute,
                Permissions.KnowledgeView, Permissions.KnowledgeManage, Permissions.ChatUse,
                Permissions.BillingView, Permissions.UsersView,
            ],
            [Analyst] =
            [
                Permissions.DatabaseView, Permissions.ToolsView, Permissions.ToolsExecute,
                Permissions.KnowledgeView, Permissions.ChatUse,
            ],
            [Viewer] = [Permissions.ToolsExecute, Permissions.KnowledgeView, Permissions.ChatUse],
        };
}
