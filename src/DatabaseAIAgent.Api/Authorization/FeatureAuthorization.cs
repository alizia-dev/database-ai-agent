using DatabaseAIAgent.Application.Abstractions.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseAIAgent.Api.Authorization;

/// <summary>
/// Requires the workspace's current plan to include a feature, e.g.
/// <c>[RequireFeature(PlanFeatures.ProductionAccess)]</c>. Evaluated server-side from the subscription on every request.
/// Combine with <see cref="RequirePermissionAttribute"/> for user-level permissions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute(string feature) : AuthorizeAttribute(FeaturePolicy.Prefix + feature)
{
    public string Feature { get; } = feature;
}

internal sealed record FeatureRequirement(string Feature) : IAuthorizationRequirement;

internal static class FeaturePolicy
{
    public const string Prefix = "feature:";
}

internal sealed class FeatureHandler(IEntitlementService entitlements) : AuthorizationHandler<FeatureRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, FeatureRequirement requirement)
    {
        var current = await entitlements.GetCurrentAsync(CancellationToken.None);
        // Not succeeding (rather than calling Fail) keeps the requirement in FailedRequirements, which the result
        // handler below uses to return an "upgrade required" response.
        if (current.HasFeature(requirement.Feature))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Turns plan-feature authorization failures into an actionable 403 (<c>billing.upgrade_required</c>) instead of a
/// bare Forbidden, so the client can offer an upgrade. All other results use the default behaviour.
/// </summary>
internal sealed class PlanAwareAuthorizationResultHandler(IProblemDetailsService problemDetails) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<FeatureRequirement>().Any() == true)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Upgrade required",
                Detail = "Your current plan does not include this feature. Choose a subscription plan to continue.",
            };
            problem.Extensions["code"] = "billing.upgrade_required";
            await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
