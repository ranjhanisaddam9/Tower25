namespace HR.Web.Security;

public static class AuthCookie
{
    public const string Name = "hr.auth";
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

        return services;
    }
}
