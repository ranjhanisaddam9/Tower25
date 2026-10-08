using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace HR.Infrastructure.Identity;

/// <summary>
/// Adds the display name and the forced-password-change flag to the cookie principal.
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
        identity.AddClaim(new Claim(AppClaimTypes.MustChangePassword, user.MustChangePassword.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()));
        return identity;
    }
}
