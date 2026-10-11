using Microsoft.AspNetCore.Identity;

namespace HR.Infrastructure.Identity;

/// <summary>An application login (Admin or Manager). The username is always the email.</summary>
public class ApplicationUser : IdentityUser
{
    public const int FullNameMaxLength = 200;

    public string FullName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>When the account was created, stored in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>True after seeding, creation or an admin reset: the user must set a new password before doing anything else.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Last successful sign-in, stored in UTC.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// Set by the Admin for a Manager (M10): the Manager must enrol in two-factor sign-in before using the app.
    /// Admins always need two-factor, whatever this says.
    /// </summary>
    public bool RequireTwoFactor { get; set; }

    /// <summary>
    /// The RFC 6238 time step of the last authenticator code accepted (M10). Codes from that step or earlier are refused,
    /// so an observed code can't be replayed within its validity window.
    /// </summary>
    public long? LastTotpTimeStep { get; set; }
}
