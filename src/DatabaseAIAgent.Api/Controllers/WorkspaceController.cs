using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Workspace;
using DatabaseAIAgent.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

[ApiController]
[Route("api/workspace/settings")]
public sealed class WorkspaceController(WorkspaceSettingsService settings) : ControllerBase
{
    /// <summary>Any signed-in user can read workspace settings (assistant name, locale) to render the app.</summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<WorkspaceSettingsDto>> Get(CancellationToken cancellationToken) =>
        Ok(await settings.GetAsync(cancellationToken));

    [HttpPut]
    [RequirePermission(Permissions.SettingsManage)]
    public async Task<ActionResult<WorkspaceSettingsDto>> Update(UpdateWorkspaceSettingsRequest request, CancellationToken cancellationToken) =>
        this.ToActionResult(await settings.UpdateAsync(request, cancellationToken));
}
