using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Api.Errors;
using DatabaseAIAgent.Application.Abstractions.Billing;
using DatabaseAIAgent.Billing.Plans;
using DatabaseAIAgent.Billing.Subscriptions;
using DatabaseAIAgent.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Controllers;

[ApiController]
[Route("api/billing")]
public sealed class BillingController(
    PlanCatalogService catalog,
    IEntitlementService entitlements,
    ISubscriptionService subscriptions,
    IInvoiceService invoices,
    IBillingService billing) : ControllerBase
{
    /// <summary>Public plan catalogue for the pricing page.</summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    public async Task<ActionResult<PlanCatalogDto>> Plans(CancellationToken cancellationToken) =>
        Ok(await catalog.GetPublicCatalogAsync(cancellationToken));

    /// <summary>What the workspace can currently do; any signed-in user (the UI adapts to it).</summary>
    [HttpGet("entitlements")]
    [Authorize]
    public async Task<ActionResult<Entitlements>> Entitlements(CancellationToken cancellationToken) =>
        Ok(await entitlements.GetCurrentAsync(cancellationToken));

    [HttpGet("subscription")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<ActionResult<SubscriptionOverviewDto>> Subscription(CancellationToken cancellationToken) =>
        Ok(await subscriptions.GetOverviewAsync(cancellationToken));

    [HttpGet("history")]
    [RequirePermission(Permissions.BillingView)]
    public async Task<ActionResult<BillingHistoryDto>> History(CancellationToken cancellationToken) =>
        Ok(await invoices.GetHistoryAsync(cancellationToken));

    /// <summary>Starts provider-hosted checkout. The subscription becomes active only when the provider's webhook confirms payment.</summary>
    [HttpPost("checkout")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult<RedirectDto>> Checkout(StartCheckoutCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await billing.StartCheckoutAsync(command, cancellationToken));

    /// <summary>Provider-hosted page for payment methods, invoices and cancellation (Stripe customer portal / PayPal autopay).</summary>
    [HttpPost("portal")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult<RedirectDto>> Portal(CancellationToken cancellationToken) =>
        this.ToActionResult(await billing.CreatePortalSessionAsync(cancellationToken));

    [HttpPost("change-plan")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult<PlanChangeDto>> ChangePlan(ChangePlanCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await billing.ChangePlanAsync(command, cancellationToken));

    [HttpPost("cancel")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult> Cancel(CancelSubscriptionCommand command, CancellationToken cancellationToken) =>
        this.ToNoContent(await billing.CancelAsync(command, cancellationToken));

    [HttpPost("resume")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult> Resume(CancellationToken cancellationToken) =>
        this.ToNoContent(await billing.ResumeAsync(cancellationToken));

    /// <summary>Operator-managed deployments only (<c>Billing:AllowPlanEditing</c>).</summary>
    [HttpPut("plans/{code}")]
    [RequirePermission(Permissions.BillingManage)]
    public async Task<ActionResult<PlanDto>> UpdatePlan(string code, UpdatePlanCommand command, CancellationToken cancellationToken) =>
        this.ToActionResult(await catalog.UpdateAsync(code, command, cancellationToken));
}
