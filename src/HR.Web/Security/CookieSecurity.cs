using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Security;

public static class CookieSecurity
{
    /// <summary>__Host- prefixed (Secure, Path=/, no Domain), like the auth cookie (M10).</summary>
    public const string AntiforgeryCookieName = "__Host-hr.af";
    public const string TempDataCookieName = "__Host-hr.tempdata";

    /// <summary>All cookies: HttpOnly, Secure, SameSite=Lax (CLAUDE.md security baseline).</summary>
    public static IServiceCollection AddSecureCookies(this IServiceCollection services)
    {
        services.Configure<CookiePolicyOptions>(options =>
        {
            options.HttpOnly = HttpOnlyPolicy.Always;
            options.Secure = CookieSecurePolicy.Always;
            options.MinimumSameSitePolicy = SameSiteMode.Lax;
        });

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
        });

        services.Configure<CookieTempDataProviderOptions>(options =>
        {
            options.Cookie.Name = TempDataCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.Cookie.IsEssential = true;
        });

        return services;
    }
}
