using DatabaseAIAgent.Api.Authentication;
using DatabaseAIAgent.Api.Authorization;
using DatabaseAIAgent.Application.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace DatabaseAIAgent.Api.Configuration;

internal static class AuthenticationExtensions
{
    /// <summary>
    /// Bearer-token authentication plus permission-based authorization. Every endpoint requires an authenticated
    /// user unless it is explicitly marked <c>[AllowAnonymous]</c>.
    /// </summary>
    public static IServiceCollection AddPlatformAuthentication(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.ConfigureOptions<JwtBearerSetup>();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddScoped<IAuthorizationHandler, FeatureHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, PlanAwareAuthorizationResultHandler>();

        return services;
    }
}
