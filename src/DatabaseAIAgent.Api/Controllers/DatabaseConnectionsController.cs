using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Configuration;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Database.Connections;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DatabaseAIAgent.Api.Controllers;

/// <summary>
/// Customer database connections. Responses never contain credentials. Creating, testing and changing connections
/// requires <c>database.manage</c> and a plan with production access.
/// </summary>
[ApiController]
[Route("api/database")]
public sealed class DatabaseConnectionsController(DatabaseConnectionService connections) : ControllerBase
{
    /// <summary>Supported engines with default ports and a least-privilege account setup script.</summary>
    [HttpGet("providers")]
    [RequirePermission(Permissions.DatabaseView)]
    public ActionResult<IReadOnlyList<DatabaseProviderDto>> Providers() => Ok(connections.ListProviders());

    [HttpGet("connections")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<IReadOnlyList<DatabaseConnectionDto>>> List(CancellationToken cancellationToken) =>
        Ok(await connections.ListAsync(cancellationToken));

    [HttpGet("connections/{id:guid}")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<DatabaseConnectionDto>> Get(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await connections.GetAsync(id, cancellationToken));

    /// <summary>Tests connection details without saving them.</summary>
    [HttpPost("connections/test")]
    [RequirePermission(Permissions.DatabaseManage)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    [EnableRateLimiting(RateLimitPolicies.ConnectionTest)]
    public async Task<ActionResult<ConnectionTestDto>> Test(ConnectionInput input, CancellationToken cancellationToken) =>
        this.ToActionResult(await connections.TestAsync(input, cancellationToken));

    [HttpPost("connections")]
    [RequirePermission(Permissions.DatabaseManage)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    [EnableRateLimiting(RateLimitPolicies.ConnectionTest)]
    [ProducesResponseType<SavedConnectionDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SavedConnectionDto>> Create(ConnectionInput input, CancellationToken cancellationToken)
    {
        var result = await connections.CreateAsync(input, cancellationToken);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : this.ToProblem(result.Error);
    }

    [HttpPut("connections/{id:guid}")]
    [RequirePermission(Permissions.DatabaseManage)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    [EnableRateLimiting(RateLimitPolicies.ConnectionTest)]
    public async Task<ActionResult<SavedConnectionDto>> Update(Guid id, ConnectionInput input, CancellationToken cancellationToken) =>
        this.ToActionResult(await connections.UpdateAsync(id, input, cancellationToken));

    [HttpPost("connections/{id:guid}/test")]
    [RequirePermission(Permissions.DatabaseManage)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    [EnableRateLimiting(RateLimitPolicies.ConnectionTest)]
    public async Task<ActionResult<SavedConnectionDto>> Retest(Guid id, CancellationToken cancellationToken) =>
        this.ToActionResult(await connections.RetestAsync(id, cancellationToken));

    /// <summary>Removes the platform's metadata and stored credentials. The customer database is not touched.</summary>
    [HttpDelete("connections/{id:guid}")]
    [RequirePermission(Permissions.DatabaseManage)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        this.ToNoContent(await connections.DeleteAsync(id, cancellationToken));
}
