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
