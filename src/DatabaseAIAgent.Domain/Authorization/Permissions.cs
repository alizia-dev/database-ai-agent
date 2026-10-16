namespace DatabaseAIAgent.Domain.Authorization;

/// <summary>
/// The application-level permission catalogue. Roles are granted subsets of these (configurable per deployment);
/// every API endpoint that is not public requires one of them.
/// </summary>
public static class Permissions
{
    public const string DatabaseView = "database.view";
    public const string DatabaseManage = "database.manage";
    public const string DatabaseAnalyze = "database.analyze";
    public const string ToolsView = "tools.view";
    public const string ToolsManage = "tools.manage";
    public const string ToolsExecute = "tools.execute";
    public const string KnowledgeView = "knowledge.view";
    public const string KnowledgeManage = "knowledge.manage";
    public const string ChatUse = "chat.use";
    public const string BillingView = "billing.view";
    public const string BillingManage = "billing.manage";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string AuditView = "audit.view";

    /// <summary>Edit workspace settings (name, assistant, locale, retention). Reading settings needs only authentication.</summary>
    public const string SettingsManage = "settings.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        DatabaseView, DatabaseManage, DatabaseAnalyze,
        ToolsView, ToolsManage, ToolsExecute,
        KnowledgeView, KnowledgeManage,
        ChatUse,
        BillingView, BillingManage,
        UsersView, UsersManage,
        AuditView,
        SettingsManage,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string permission) => Known.Contains(permission);
}
