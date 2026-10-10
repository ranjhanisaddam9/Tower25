using HR.Infrastructure.Identity;
using HR.Web.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HR.Web.Security;

/// <summary>
/// The two-factor enrolment gate (M10): a signed-in user whose account requires two-factor (every Admin, and Managers
/// the Admin chose) but hasn't enrolled yet is sent to the setup page before anything else. Only setup itself, sign-out,
/// the forced password change (which comes first) and error pages stay reachable. Fails closed: a missing claim counts
/// as "not enrolled".
/// </summary>
public sealed class RequireTwoFactorMiddleware(RequestDelegate next)
{
    private static readonly HashSet<(string Controller, string Action)> Allowed =
    [
        ("Account", nameof(AccountController.TwoFactorSetup)),
        ("Account", nameof(AccountController.Logout)),
        ("Account", nameof(AccountController.LogoutGet)),
        ("Account", nameof(AccountController.ChangePassword)),
    ];

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(AppClaimTypes.TwoFactorRequired, "true")
            && !context.User.HasClaim(AppClaimTypes.TwoFactorEnabled, "true")
            && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
            && action.ControllerName != "Error"
            && !Allowed.Contains((action.ControllerName, action.ActionName)))
        {
            context.Response.Redirect(AccountController.SetupPath);
            return Task.CompletedTask;
        }

        return next(context);
    }
}
