using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>
/// Friendly status pages for re-executed errors (UseStatusCodePagesWithReExecute / UseExceptionHandler).
/// They never show exception details, stack traces or request internals.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken] // Re-execution keeps the original method; an error page changes no state.
[Route("error")]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorController : Controller
{
    [Route("{statusCode:int}")]
    public IActionResult Status(int statusCode)
    {
        if (statusCode is < 400 or > 599)
        {
            statusCode = 404;
        }

        Response.StatusCode = statusCode;
        return View("Status", Describe(statusCode));
    }

    private static ErrorViewModel Describe(int statusCode) => statusCode switch
    {
        400 => new(400, "Bad request", "The request could not be processed. Please go back and try again.", "bi-exclamation-octagon"),
        403 => new(403, "Access denied", "You don't have permission to view this page.", "bi-shield-lock"),
        404 => new(404, "Page not found", "The page you're looking for doesn't exist or has moved.", "bi-signpost-split"),
        405 => new(405, "Not allowed", "That action isn't allowed here.", "bi-slash-circle"),
        429 => new(429, "Too many attempts", "You've made too many requests in a short time. Please wait a minute and try again.", "bi-hourglass-split"),
        >= 500 => new(statusCode, "Something went wrong", "An unexpected error occurred. Please try again in a moment.", "bi-cloud-slash"),
        _ => new(statusCode, "Request failed", "The request could not be completed.", "bi-exclamation-circle"),
    };
}
