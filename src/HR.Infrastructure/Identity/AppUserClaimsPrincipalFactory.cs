using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HR.Infrastructure.Identity;

/// <summary>
/// Adds the display name, the forced-password-change flag and the two-factor state to the cookie principal.
/// The principal is rebuilt from the database on every security-stamp validation and on refresh sign-in,
/// so these claims never stay stale for long.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaimTypes.FullName, user.FullName));
        identity.AddClaim(new Claim(AppClaimTypes.MustChangePassword, Flag(user.MustChangePassword)));
        identity.AddClaim(new Claim(AppClaimTypes.TwoFactorEnabled, Flag(user.TwoFactorEnabled)));
        var required = AccountService.IsTwoFactorRequired(await UserManager.IsInRoleAsync(user, AppRoles.Admin), user);
        identity.AddClaim(new Claim(AppClaimTypes.TwoFactorRequired, Flag(required)));
        return identity;
    }

    private static string Flag(bool value) => value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
}
