using System.ComponentModel.DataAnnotations;

namespace HR.Web.ViewModels;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Enter your email.")]
    [Display(Name = "Email")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter your password.")]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    [StringLength(128)]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

public sealed class TwoFactorLoginViewModel
{
    [Required(ErrorMessage = "Enter the 6-digit code from your authenticator app.")]
    [Display(Name = "Authenticator code")]
    [StringLength(12)]
    public string Code { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

public sealed class RecoveryCodeLoginViewModel
{
    [Required(ErrorMessage = "Enter one of your recovery codes.")]
    [Display(Name = "Recovery code")]
    [StringLength(32)]
    public string RecoveryCode { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}

public sealed class TwoFactorSetupForm
{
    [Required(ErrorMessage = "Enter the 6-digit code your app shows.")]
    [Display(Name = "Code from the app")]
    [StringLength(12)]
    public string Code { get; set; } = string.Empty;
}

/// <param name="QrCodeDataUri">A PNG data URI of the otpauth:// link.</param>
public sealed record TwoFactorSetupViewModel(string FormattedKey, string QrCodeDataUri, bool Required, bool AlreadyEnabled, TwoFactorSetupForm Form);

public sealed record RecoveryCodesViewModel(IReadOnlyList<string> Codes, bool JustEnrolled);

public sealed class ChangePasswordViewModel
{
    public const int MinLength = 10;

    [Required(ErrorMessage = "Enter your current password.")]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    [StringLength(128)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a new password.")]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    [StringLength(128, MinimumLength = MinLength, ErrorMessage = "The new password must be at least 10 characters.")]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Confirm the new password.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    [Compare(nameof(NewPassword), ErrorMessage = "The passwords don't match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
