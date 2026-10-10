using HR.Infrastructure.Identity;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QRCoder;

namespace HR.Web.Controllers;

[Route("account")]
public class AccountController(AccountService accounts) : Controller
{
    /// <summary>Same text for unknown email, wrong password, inactive account and lockout: never hints which.</summary>
    public const string GenericLoginError = "Email or password is incorrect, or the account is temporarily locked. Please try again later.";

    public const string GenericCodeError = "That code didn't work, or the account is temporarily locked. Check the code and try again.";

    public const string TwoFactorPath = "/account/login-2fa";
    public const string SetupPath = "/account/two-factor/setup";

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
            case LoginOutcome.RequiresTwoFactor:
                return LocalRedirect(TwoFactorPath + (Url.IsLocalUrl(model.ReturnUrl) ? "?returnUrl=" + Uri.EscapeDataString(model.ReturnUrl!) : string.Empty));
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

    // ===================== Second step (two-factor) =====================

    [HttpGet("login-2fa")]
    [AllowAnonymous]
    public IActionResult LoginTwoFactor(string? returnUrl) => View(new TwoFactorLoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("login-2fa")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimit.PolicyName)]
    public async Task<IActionResult> LoginTwoFactor(TwoFactorLoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(new TwoFactorLoginViewModel { ReturnUrl = model.ReturnUrl });
        }

        return AfterSecondStep(await accounts.TwoFactorSignInAsync(model.Code, ClientIp.Of(HttpContext)), model.ReturnUrl, nameof(LoginTwoFactor));
    }

    [HttpGet("login-recovery")]
    [AllowAnonymous]
    public IActionResult LoginRecovery(string? returnUrl) => View(new RecoveryCodeLoginViewModel { ReturnUrl = returnUrl });

    [HttpPost("login-recovery")]
    [AllowAnonymous]
    [EnableRateLimiting(LoginRateLimit.PolicyName)]
    public async Task<IActionResult> LoginRecovery(RecoveryCodeLoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(new RecoveryCodeLoginViewModel { ReturnUrl = model.ReturnUrl });
        }

        return AfterSecondStep(await accounts.RecoveryCodeSignInAsync(model.RecoveryCode, ClientIp.Of(HttpContext)), model.ReturnUrl, nameof(LoginRecovery));
    }

    private IActionResult AfterSecondStep(LoginOutcome outcome, string? returnUrl, string view)
    {
        switch (outcome)
        {
            case LoginOutcome.MustChangePassword:
                return LocalRedirect(ForcePasswordChangeMiddleware.ChangePasswordPath);
            case LoginOutcome.Succeeded:
                return SafeRedirect(returnUrl);
            default:
                ModelState.Clear();
                ModelState.AddModelError(string.Empty, GenericCodeError);
                return view == nameof(LoginRecovery)
                    ? View(view, new RecoveryCodeLoginViewModel { ReturnUrl = returnUrl })
                    : View(view, new TwoFactorLoginViewModel { ReturnUrl = returnUrl });
        }
    }

    // ===================== Sign out, password =====================

    /// <summary>Signing out changes state, so it is POST-only; a GET (e.g. a forged link or image) is refused.</summary>
    [HttpGet("logout")]
    public IActionResult LogoutGet()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    /// <summary>Ends this session and, by rotating the security stamp, every other session of the same user.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await accounts.SignOutAsync(User);
        return LocalRedirect(AuthCookie.LoginPath);
    }

    [HttpGet("change-password")]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost("change-password")]
    [EnableRateLimiting(ChangePasswordRateLimit.PolicyName)]
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

    // ===================== Two-factor enrolment and management =====================

    [HttpGet("security")]
    public async Task<IActionResult> Security()
    {
        var status = await accounts.GetTwoFactorStatusAsync(User);
        return status is null ? Challenge() : View(status);
    }

    [HttpGet("two-factor/setup")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> TwoFactorSetup() => await SetupViewAsync(new TwoFactorSetupForm());

    [HttpPost("two-factor/setup")]
    [EnableRateLimiting(ChangePasswordRateLimit.PolicyName)]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> TwoFactorSetup(TwoFactorSetupForm form)
    {
        if (!ModelState.IsValid)
        {
            return await SetupViewAsync(new TwoFactorSetupForm());
        }

        if ((await accounts.GetTwoFactorStatusAsync(User))?.Enabled == true)
        {
            return RedirectToAction(nameof(Security));
        }

        var codes = await accounts.EnableTwoFactorAsync(User, form.Code);
        if (codes is null)
        {
            ModelState.AddModelError(nameof(TwoFactorSetupForm.Code), "That code didn't match. Check the time on your phone and try the newest code.");
            return await SetupViewAsync(new TwoFactorSetupForm());
        }

        // Shown once, in this response only (never stored, never sent through TempData).
        return View("RecoveryCodes", new RecoveryCodesViewModel(codes, JustEnrolled: true));
    }

    [HttpPost("two-factor/recovery-codes")]
    [EnableRateLimiting(ChangePasswordRateLimit.PolicyName)]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> RegenerateRecoveryCodes()
    {
        var codes = await accounts.RegenerateRecoveryCodesAsync(User);
        if (codes is null)
        {
            TempData.ToastError("Turn on two-factor sign-in first.");
            return RedirectToAction(nameof(Security));
        }

        return View("RecoveryCodes", new RecoveryCodesViewModel(codes, JustEnrolled: false));
    }

    [HttpPost("two-factor/disable")]
    public async Task<IActionResult> DisableTwoFactor()
    {
        if (await accounts.DisableTwoFactorAsync(User))
        {
            TempData.ToastSuccess("Two-factor sign-in is off.");
        }
        else
        {
            TempData.ToastError("Two-factor sign-in is required for your account, so it can't be turned off.");
        }

        return RedirectToAction(nameof(Security));
    }

    private async Task<IActionResult> SetupViewAsync(TwoFactorSetupForm form)
    {
        var status = await accounts.GetTwoFactorStatusAsync(User);
        if (status is null)
        {
            return Challenge();
        }

        // Never show an enrolled user's secret again: to change devices, turn it off (or ask the Admin) and enrol anew.
        if (status.Enabled)
        {
            return RedirectToAction(nameof(Security));
        }

        var setup = (await accounts.GetAuthenticatorSetupAsync(User))!;
        return View("TwoFactorSetup", new TwoFactorSetupViewModel(setup.FormattedKey, QrDataUri(setup.Uri), status.Required, false, form));
    }

    /// <summary>A PNG data URI: the CSP allows <c>img-src data:</c>, and no markup is generated from user data.</summary>
    private static string QrDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    /// <summary>Only same-site paths are followed; anything else (absolute, protocol-relative, backslash tricks) goes home.</summary>
    private IActionResult SafeRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : LocalRedirect("/");
}
