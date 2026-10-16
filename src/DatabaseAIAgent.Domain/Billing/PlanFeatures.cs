namespace DatabaseAIAgent.Domain.Billing;

/// <summary>
/// Feature flags a plan can grant. Feature checks are always made server-side against the active entitlements.
/// </summary>
public static class PlanFeatures
{
    /// <summary>Connect a real customer database and run production tools/chat (everything except the demo).</summary>
    public const string ProductionAccess = "production_access";

    public const string DemoEnvironment = "demo_environment";
    public const string Rag = "rag";
    public const string AdvancedRag = "advanced_rag";
    public const string Analytics = "analytics";
    public const string AdvancedAnalytics = "advanced_analytics";
    public const string Rbac = "rbac";
    public const string AdvancedPermissions = "advanced_permissions";
    public const string BasicAuditLogs = "basic_audit_logs";
    public const string AdvancedAuditLogs = "advanced_audit_logs";
    public const string MultipleAiProviders = "multiple_ai_providers";
    public const string CustomTerminology = "custom_terminology";
    public const string CustomToolConfiguration = "custom_tool_configuration";
    public const string ApiAccess = "api_access";
    public const string Sso = "sso";
    public const string Mfa = "mfa";
    public const string PrivateDeployment = "private_deployment";
    public const string PrivateNetworking = "private_networking";
    public const string LocalLlm = "local_llm";
    public const string CustomModels = "custom_models";
    public const string CustomRetention = "custom_retention";
    public const string EmailSupport = "email_support";
    public const string PrioritySupport = "priority_support";
    public const string DedicatedOnboarding = "dedicated_onboarding";
    public const string DedicatedSupport = "dedicated_support";
    public const string Sla = "sla";

    public static IReadOnlyList<string> All { get; } =
    [
        ProductionAccess, DemoEnvironment, Rag, AdvancedRag, Analytics, AdvancedAnalytics, Rbac, AdvancedPermissions,
        BasicAuditLogs, AdvancedAuditLogs, MultipleAiProviders, CustomTerminology, CustomToolConfiguration, ApiAccess,
        Sso, Mfa, PrivateDeployment, PrivateNetworking, LocalLlm, CustomModels, CustomRetention, EmailSupport,
        PrioritySupport, DedicatedOnboarding, DedicatedSupport, Sla,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string feature) => Known.Contains(feature);
}
