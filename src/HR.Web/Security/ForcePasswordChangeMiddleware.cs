using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace HR.Web.Security;

/// <summary>
/// While the signed-in user's MustChangePassword flag is set, every page except change password, logout and
/// the error pages redirects to the change-password page. Static files are not controller actions and pass through.
/// </summary>
public sealed class ForcePasswordChangeMiddleware(RequestDelegate next)
{
    public const string ChangePasswordPath = "/account/change-password";

    private static readonly HashSet<(string Controller, string Action)> Allowed =
    [
        ("Account", "ChangePassword"),
        ("Account", "Logout"),
    ];

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(AppClaimTypes.MustChangePassword, "true")
            && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
            && action.ControllerName != "Error"
            && !Allowed.Contains((action.ControllerName, action.ActionName)))
        {
            context.Response.Redirect(ChangePasswordPath);
            return Task.CompletedTask;
        }

        return next(context);
    }
}
