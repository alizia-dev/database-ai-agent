using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Domain.Billing;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Tests.Billing;

/// <summary>A test-only endpoint guarded exactly like future production endpoints (e.g. connecting a real database).</summary>
[ApiController]
[Route("api/__test/production-feature")]
[RequireFeature(PlanFeatures.ProductionAccess)]
public sealed class TestFeatureController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { ok = true });
}
