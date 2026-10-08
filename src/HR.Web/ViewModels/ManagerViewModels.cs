using System.ComponentModel.DataAnnotations;
using HR.Infrastructure.Identity;

namespace HR.Web.ViewModels;

public sealed record ManagerRowViewModel(string Id, string FullName, string Email, bool IsActive, bool MustChangePassword, DateTimeOffset? LastLoginAt);

public sealed record ManagerListViewModel(
    IReadOnlyList<ManagerRowViewModel> Managers,
    string? Search,
    ManagerStatusFilter Status,
    int Page,
    int TotalPages,
    int TotalCount)
{
    public bool HasFilter => !string.IsNullOrWhiteSpace(Search) || Status != ManagerStatusFilter.All;
}

/// <summary>Create and edit form. Only these two fields can be posted (no over-posting of roles or flags).</summary>
public sealed class ManagerFormViewModel
{
    [Required(ErrorMessage = "Enter the full name.")]
    [StringLength(ApplicationUser.FullNameMaxLength, ErrorMessage = "The full name can be at most 200 characters.")]
    [Display(Name = "Full name")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the email.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256)]
    [Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;
}

public sealed record ManagerEditViewModel(string Id, bool IsActive, ManagerFormViewModel Form);

/// <summary>Shown exactly once, straight from the POST that generated the password. Never stored or logged.</summary>
public sealed record TemporaryPasswordViewModel(string FullName, string Email, string TemporaryPassword, bool IsReset);
