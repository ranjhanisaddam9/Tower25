using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HR.Infrastructure.Identity;

/// <summary>
/// Runs on every cookie validation interval: rejects the session when the security stamp changed
/// (password reset, deactivation, email change) or when the user is no longer active.
/// </summary>
public sealed class ActiveUserSecurityStampValidator(
    IOptions<SecurityStampValidatorOptions> options,
    SignInManager<ApplicationUser> signInManager,
    ILoggerFactory logger)
    : SecurityStampValidator<ApplicationUser>(options, signInManager, logger)
{
    protected override async Task<ApplicationUser?> VerifySecurityStamp(ClaimsPrincipal? principal)
    {
        var user = await base.VerifySecurityStamp(principal);
        return user is { IsActive: true } ? user : null;
    }
}
