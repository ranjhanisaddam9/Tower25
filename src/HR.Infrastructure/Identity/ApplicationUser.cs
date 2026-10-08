using Microsoft.AspNetCore.Identity;

namespace HR.Infrastructure.Identity;

/// <summary>An application login (Admin or Manager). Roles arrive in M2.</summary>
public class ApplicationUser : IdentityUser
{
    public const int FullNameMaxLength = 200;

    public string FullName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>When the account was created, stored in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
