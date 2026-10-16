namespace DatabaseAIAgent.Api.Configuration;

internal static class RateLimitPolicies
{
    /// <summary>Credential and email-sending endpoints (login, register, password reset, invitations).</summary>
    public const string Auth = "auth";

    /// <summary>Token refresh (called automatically by the client, so allowed more often than <see cref="Auth"/>).</summary>
    public const string Refresh = "refresh";
}
