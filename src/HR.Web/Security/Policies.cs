using HR.Infrastructure.Identity;

namespace HR.Web.Security;

/// <summary>Named authorization policies. Apply with [Authorize(Policy = Policies.AdminOnly)].</summary>
public static class Policies
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string ManagerOrAdmin = nameof(ManagerOrAdmin);

    public static IServiceCollection AddAppAuthorization(this IServiceCollection services)
    {
        // The authenticated-by-default fallback policy is switched on in M2 together with login;
        // until then there is no authentication scheme to challenge with.
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminOnly, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin))
            .AddPolicy(ManagerOrAdmin, policy => policy.RequireAuthenticatedUser().RequireRole(AppRoles.Admin, AppRoles.Manager));

        return services;
    }
}
