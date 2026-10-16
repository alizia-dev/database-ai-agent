using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Database;

public sealed class DatabaseConnectionOptions
{
    public const string SectionName = "DatabaseConnections";

    /// <summary>
    /// Allow targets on private networks (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7). Enable for deployments
    /// inside the customer's network (VPC, on-premises). Off for internet-facing SaaS, so users cannot probe the
    /// platform's own network.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>Allow loopback targets (localhost). Development only.</summary>
    public bool AllowLoopback { get; set; }

    [Range(3, 60)]
    public int ConnectTimeoutSeconds { get; set; } = 15;

    [Range(3, 120)]
    public int TestCommandTimeoutSeconds { get; set; } = 15;
}
