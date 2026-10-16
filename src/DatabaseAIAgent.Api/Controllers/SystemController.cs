using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

[ApiController]
[Route("api/system")]
[AllowAnonymous]
public sealed class SystemController(IHostEnvironment environment) : ControllerBase
{
    /// <summary>Public, non-sensitive build information used by the web client to confirm API reachability.</summary>
    [HttpGet("info")]
    [ProducesResponseType<SystemInfoResponse>(StatusCodes.Status200OK)]
    public ActionResult<SystemInfoResponse> GetInfo()
    {
        var version = typeof(SystemController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";

        // Strip the source-revision suffix ("1.0.0+abc123") so internal build details are not disclosed.
        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            version = version[..plusIndex];
        }

        return Ok(new SystemInfoResponse("Database AI Agent", version, environment.IsProduction() ? "Production" : environment.EnvironmentName));
    }
}

public sealed record SystemInfoResponse(string Name, string Version, string Environment);
