using System.Text.Json;
using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Configuration;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Abstractions.Tools;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Billing;
using DatabaseAIAgent.Domain.Tools;
using DatabaseAIAgent.Tools.Management;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DatabaseAIAgent.Api.Controllers;

/// <param name="Arguments">Tool arguments as a JSON object, keyed by the tool's parameter names.</param>
/// <param name="Confirmed">Set after the user confirmed a WRITE/DELETE execution.</param>
public sealed record ExecuteToolRequest(JsonElement Arguments, bool Confirmed);

/// <summary>
/// AI tools generated from approved stored procedures. Tools are drafted automatically after schema analysis and
/// must be approved by a user with <c>tools.manage</c> before the assistant can call them.
/// </summary>
[ApiController]
[Route("api")]
public sealed class ToolsController(ToolManagementService management, IToolExecutor executor) : ControllerBase
{
    [HttpGet("tools")]
    [RequirePermission(Permissions.ToolsView)]
    public async Task<ActionResult<IReadOnlyList<ToolSummaryDto>>> List(
        [FromQuery] Guid? connectionId, [FromQuery] ToolStatus? status, [FromQuery] string? search, CancellationToken cancellationToken) =>
        Ok(await management.ListAsync(connectionId, status, search, cancellationToken));

    [HttpGet("tools/{id:guid}")]
    [RequirePermission(Permissions.ToolsView)]
    public async Task<ActionResult<ToolDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.GetAsync(id, cancellationToken));

    /// <summary>Regenerates tool drafts from the latest analyzed catalog (also runs after every analysis).</summary>
    [HttpPost("database/connections/{connectionId:guid}/tools/generate")]
    [RequirePermission(Permissions.ToolsManage)]
    public async Task<ActionResult<ToolSyncReport>> Generate(Guid connectionId, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.GenerateAsync(connectionId, cancellationToken));

    [HttpPut("tools/{id:guid}")]
    [RequirePermission(Permissions.ToolsManage)]
    public async Task<ActionResult<ToolDetailDto>> Update(Guid id, UpdateToolCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.UpdateAsync(id, command, cancellationToken));

    [HttpPost("tools/{id:guid}/approve")]
    [RequirePermission(Permissions.ToolsManage)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    public async Task<ActionResult<ToolDetailDto>> Approve(Guid id, ApproveToolCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.ApproveAsync(id, command, cancellationToken));

    [HttpPost("tools/{id:guid}/disable")]
    [RequirePermission(Permissions.ToolsManage)]
    public async Task<ActionResult<ToolDetailDto>> Disable(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.DisableAsync(id, cancellationToken));

    [HttpPost("tools/{id:guid}/reject")]
    [RequirePermission(Permissions.ToolsManage)]
    public async Task<ActionResult<ToolDetailDto>> Reject(Guid id, RejectToolCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.RejectAsync(id, command, cancellationToken));

    [HttpPost("tools/{id:guid}/reopen")]
    [RequirePermission(Permissions.ToolsManage)]
    public async Task<ActionResult<ToolDetailDto>> Reopen(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.ReopenAsync(id, cancellationToken));

    /// <summary>Confirms that a routine whose definition is not visible only reads data.</summary>
    [HttpPost("tools/{id:guid}/confirm-read-only")]
    [RequirePermission(Permissions.ToolsManage)]
    public async Task<ActionResult<ToolDetailDto>> ConfirmReadOnly(Guid id, ConfirmReadOnlyCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await management.ConfirmReadOnlyAsync(id, command, cancellationToken));

    /// <summary>
    /// Runs an enabled tool through the full execution pipeline (the same one the assistant uses). Outcomes other than
    /// success are returned as 200 with an <c>outcome</c>, since they are expected results rather than HTTP errors.
    /// </summary>
    [HttpPost("tools/{name}/execute")]
    [RequirePermission(Permissions.ToolsExecute)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    [EnableRateLimiting(RateLimitPolicies.ToolExecution)]
    public async Task<ActionResult<ToolExecutionResult>> Execute(string name, ExecuteToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ok(await executor.ExecuteAsync(new ToolExecutionRequest(name, request.Arguments, request.Confirmed, null), cancellationToken));
    }
}
