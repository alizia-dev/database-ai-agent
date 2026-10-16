using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Api.Authorization;

/// <summary>Requires the caller to hold a permission, e.g. <c>[RequirePermission(Permissions.UsersManage)]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute(PermissionPolicy.Prefix + permission)
{
    public string Permission { get; } = permission;
}

internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

internal static class PermissionPolicy
{
    public const string Prefix = "permission:";
}

/// <summary>Builds <c>permission:*</c> and <c>feature:*</c> policies on demand, so none has to be registered individually.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(FeaturePolicy.Prefix, StringComparison.Ordinal))
        {
            var feature = policyName[FeaturePolicy.Prefix.Length..];
            if (!Domain.Billing.PlanFeatures.IsKnown(feature))
            {
                throw new InvalidOperationException($"Unknown plan feature '{feature}' used in an authorization attribute.");
            }

            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new FeatureRequirement(feature))
                .Build();
        }

        if (!policyName.StartsWith(PermissionPolicy.Prefix, StringComparison.Ordinal))
        {
            return await base.GetPolicyAsync(policyName);
        }

        var permission = policyName[PermissionPolicy.Prefix.Length..];
        if (!Domain.Authorization.Permissions.IsKnown(permission))
        {
            throw new InvalidOperationException($"Unknown permission '{permission}' used in an authorization attribute.");
        }

        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission))
            .Build();
    }
}

/// <summary>Checks the permission against the database-backed permission service (never against token claims).</summary>
internal sealed class PermissionHandler(IPermissionService permissions, ICurrentUser currentUser) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (currentUser.UserId is { } userId
            && await permissions.HasPermissionAsync(userId, requirement.Permission, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}
