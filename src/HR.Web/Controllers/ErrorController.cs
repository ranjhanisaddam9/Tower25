using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Diagnostics;
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
public class ErrorController(ILogger<ErrorController> logger) : Controller
{
    [Route("{statusCode:int}")]
    public IActionResult Status(int statusCode)
    {
        if (statusCode is < 400 or > 599)
        {
            statusCode = 404;
        }

        // An unhandled exception re-executes /error/500; a database outage is reported as 503 instead.
        if (HttpContext.Features.Get<IExceptionHandlerFeature>() is { } handled)
        {
            statusCode = Hardening.IsDatabaseUnavailable(handled.Error) ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
        }

        Response.StatusCode = statusCode;
        var model = Describe(statusCode);
        if (statusCode >= 500)
        {
            // The same id the request log and the exception handler's log entry carry; never exception text.
            var correlationId = Hardening.CorrelationId(HttpContext);
            if (HttpContext.Features.Get<IExceptionHandlerFeature>() is { } failure)
            {
                logger.LogError("Request failed with {StatusCode} (correlation {CorrelationId}) on {Path}: {ExceptionType}",
                    statusCode, correlationId, failure.Path, failure.Error.GetType().Name);
            }

            model = model with { CorrelationId = correlationId };
        }

        return View("Status", model);
    }

    private static ErrorViewModel Describe(int statusCode) => statusCode switch
    {
        400 => new(400, "Bad request", "The request could not be processed. Please go back and try again.", "bi-exclamation-octagon"),
        403 => new(403, "Access denied", "You don't have permission to view this page.", "bi-shield-lock"),
        404 => new(404, "Page not found", "The page you're looking for doesn't exist or has moved.", "bi-signpost-split"),
        405 => new(405, "Not allowed", "That action isn't allowed here.", "bi-slash-circle"),
        429 => new(429, "Too many attempts", "You've made too many requests in a short time. Please wait a minute and try again.", "bi-hourglass-split"),
        503 => new(503, "Temporarily unavailable", "The service can't reach its database right now. Please try again in a few minutes; if it continues, tell the administrator the reference below.", "bi-database-exclamation"),
        >= 500 => new(statusCode, "Something went wrong", "An unexpected error occurred. Please try again in a moment.", "bi-cloud-slash"),
        _ => new(statusCode, "Request failed", "The request could not be completed.", "bi-exclamation-circle"),
    };
}
