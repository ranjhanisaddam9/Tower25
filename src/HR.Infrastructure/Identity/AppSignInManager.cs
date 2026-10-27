using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HR.Infrastructure.Identity;

/// <summary>
/// Deactivated users can never sign in (PasswordSignInAsync then returns NotAllowed). With <see cref="TwoFactorSwitch"/> on
/// (Development only) the authenticator step is skipped.
/// </summary>
public sealed class AppSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    TwoFactorSwitch twoFactor)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(ApplicationUser user) =>
        user.IsActive && await base.CanSignInAsync(user);

    public override async Task<bool> IsTwoFactorEnabledAsync(ApplicationUser user) =>
        !twoFactor.Disabled && await base.IsTwoFactorEnabledAsync(user);
}
