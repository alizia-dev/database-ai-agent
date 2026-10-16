using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Tools;

public sealed class ToolsOptions
{
    public const string SectionName = "Tools";

    /// <summary>
    /// Allow WRITE and DELETE tools to be approved. Off by default (the MVP exposes read-only tools only). When on,
    /// such tools still need explicit approval, an allowed role, and the user's confirmation before every execution.
    /// ADMIN tools (schema/permission changes) can never be enabled.
    /// </summary>
    public bool AllowWriteTools { get; set; }

    [Range(1, 300)]
    public int DefaultTimeoutSeconds { get; set; } = 30;

    [Range(1, 10_000)]
    public int DefaultMaxRows { get; set; } = 500;

    /// <summary>Default maximum string length accepted for text parameters without a declared length.</summary>
    [Range(16, 100_000)]
    public int DefaultMaxTextLength { get; set; } = 4000;
}
