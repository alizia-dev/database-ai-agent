using System.ComponentModel.DataAnnotations;

namespace DatabaseAIAgent.Application.Common;

public sealed class AppUrlOptions
{
    public const string SectionName = "App";

    /// <summary>Public URL of the web app, used to build links in emails and payment return URLs (e.g. https://app.example.com).</summary>
    [Required, Url]
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Absolute URL for an in-app path such as "/app/billing".</summary>
    public string Link(string path) => $"{PublicUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
