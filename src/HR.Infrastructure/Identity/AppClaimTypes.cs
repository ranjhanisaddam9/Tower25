namespace HR.Infrastructure.Identity;

/// <summary>Custom claims added to the auth cookie by <see cref="AppUserClaimsPrincipalFactory"/>.</summary>
public static class AppClaimTypes
{
    public const string FullName = "hr:full_name";
    public const string MustChangePassword = "hr:must_change_password";
    public const string TwoFactorEnabled = "hr:2fa";
    public const string TwoFactorRequired = "hr:2fa_required";
}
