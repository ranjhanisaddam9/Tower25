using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;

namespace HR.Web.Security;

/// <summary>Named authorization policies. Apply with [Authorize(Policy = Policies.AdminOnly)].</summary>
public static class Policies
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string ManagerOrAdmin = nameof(ManagerOrAdmin);

    public static IServiceCollection AddAppAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminOnly, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin))
            .AddPolicy(ManagerOrAdmin, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin, AppRoles.Manager))
            // Everything requires a signed-in user unless it opts out with [AllowAnonymous]
            // (login, error pages and static files only).
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }
}
