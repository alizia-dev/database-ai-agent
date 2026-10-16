using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Database.Discovery;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

/// <summary>Schema analysis (runs in the background) and the stored metadata catalog. No customer data is returned.</summary>
[ApiController]
[Route("api/database/connections/{connectionId:guid}")]
public sealed class SchemaController(SchemaAnalysisService analysis) : ControllerBase
{
    /// <summary>Queues an analysis. Returns 202; poll the overview for progress.</summary>
    [HttpPost("analyze")]
    [RequirePermission(Permissions.DatabaseAnalyze)]
    [RequireFeature(PlanFeatures.ProductionAccess)]
    [ProducesResponseType<AnalysisDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<AnalysisDto>> Analyze(Guid connectionId, CancellationToken cancellationToken)
    {
        var result = await analysis.StartAsync(connectionId, cancellationToken);
        return result.IsSuccess ? Accepted(result.Value) : this.ToProblem(result.Error);
    }

    [HttpGet("schema")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<SchemaOverviewDto>> Overview(Guid connectionId, CancellationToken cancellationToken) =>
        this.ToActionResult(await analysis.GetOverviewAsync(connectionId, cancellationToken));

    [HttpGet("tables")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<PagedResult<TableSummary>>> Tables(
        Guid connectionId, [FromQuery] string? schema, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        this.ToActionResult(await analysis.SearchTablesAsync(connectionId, schema, search, page, pageSize, cancellationToken));

    [HttpGet("tables/{tableId:guid}")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<TableDetailDto>> Table(Guid connectionId, Guid tableId, CancellationToken cancellationToken) =>
        this.ToActionResult(await analysis.GetTableAsync(connectionId, tableId, cancellationToken));

    [HttpGet("procedures")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<PagedResult<ProcedureSummary>>> Procedures(
        Guid connectionId, [FromQuery] string? schema, [FromQuery] string? search, [FromQuery] bool includeMissing = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default) =>
        this.ToActionResult(await analysis.SearchProceduresAsync(connectionId, schema, search, includeMissing, page, pageSize, cancellationToken));

    [HttpGet("procedures/{procedureId:guid}")]
    [RequirePermission(Permissions.DatabaseView)]
    public async Task<ActionResult<ProcedureDetailDto>> Procedure(Guid connectionId, Guid procedureId, CancellationToken cancellationToken) =>
        this.ToActionResult(await analysis.GetProcedureAsync(connectionId, procedureId, cancellationToken));
}
