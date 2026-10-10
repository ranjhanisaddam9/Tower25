using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace HR.Web.Security;

public static class AuthCookie
{
    /// <summary>
    /// The __Host- prefix makes browsers accept the cookie only when it is Secure, has Path=/ and no Domain, so a
    /// sibling sub-domain can't set or overwrite it (M10).
    /// </summary>
    public const string Name = "__Host-hr.auth";
    public const string TwoFactorName = "__Host-hr.2fa";
    public const string LoginPath = "/account/login";
    public const string AccessDeniedPath = "/error/403";

    /// <summary>The Identity application cookie: HttpOnly, Secure, SameSite=Lax, 8-hour sliding session.</summary>
    public static IServiceCollection AddAppAuthCookie(this IServiceCollection services)
    {
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = Name;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.Cookie.Domain = null;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = LoginPath;
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = AccessDeniedPath;
            options.ReturnUrlParameter = "ReturnUrl";

            // Answer "forbidden" with a real 403 on the requested URL; the status-code page middleware then
            // renders /error/403 in place, instead of a 302 that would hide the status from clients and tests.
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        // The short-lived "password checked, waiting for the code" cookie of the two-factor step.
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.Name = TwoFactorName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });

        return services;
    }
}
