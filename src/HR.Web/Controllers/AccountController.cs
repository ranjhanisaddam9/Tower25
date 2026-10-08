using HR.Infrastructure.Identity;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HR.Web.Controllers;

[Route("account")]
public class AccountController(AccountService accounts) : Controller
{
    /// <summary>Same text for unknown email, wrong password, inactive account and lockout: never hints which.</summary>
    public const string GenericLoginError = "Email or password is incorrect, or the account is temporarily locked. Please try again later.";

    [HttpGet("login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return SafeRedirect(returnUrl);
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimit.PolicyName)]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var outcome = await accounts.SignInAsync(model.Email, model.Password, ClientIp.Of(HttpContext));
        switch (outcome)
        {
            case LoginOutcome.MustChangePassword:
                return LocalRedirect(ForcePasswordChangeMiddleware.ChangePasswordPath);
            case LoginOutcome.Succeeded:
                return SafeRedirect(model.ReturnUrl);
            default:
                ModelState.Clear();
                ModelState.AddModelError(string.Empty, GenericLoginError);
                return View(new LoginViewModel { Email = model.Email, ReturnUrl = model.ReturnUrl });
        }
    }

    /// <summary>Signing out changes state, so it is POST-only; a GET (e.g. a forged link or image) is refused.</summary>
    [HttpGet("logout")]
    public IActionResult LogoutGet()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await accounts.SignOutAsync(User);
        return LocalRedirect(AuthCookie.LoginPath);
    }

    [HttpGet("change-password")]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(new ChangePasswordViewModel());
        }

        var result = await accounts.ChangePasswordAsync(User, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return View(new ChangePasswordViewModel());
        }

        TempData.ToastSuccess("Your password has been changed.");
        return LocalRedirect("/");
    }

    /// <summary>Only same-site paths are followed; anything else (absolute, protocol-relative, backslash tricks) goes home.</summary>
    private IActionResult SafeRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect("/");
}
