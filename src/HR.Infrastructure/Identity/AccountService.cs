using System.Security.Claims;
using HR.Domain.Time;
using HR.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Identity;

public enum LoginOutcome
{
    /// <summary>Signed in; continue to the requested page.</summary>
    Succeeded,

    /// <summary>Signed in, but the user must set a new password first.</summary>
    MustChangePassword,

    /// <summary>Not signed in. Callers show one generic message whatever the reason.</summary>
    Failed,
}

public sealed record ChangePasswordResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>Sign-in, sign-out and password change for the signed-in user, with security-event logging.</summary>
public sealed class AccountService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IClock clock,
    ILoggerFactory loggerFactory)
{
    private const string UnknownUser = "(unknown)";

    // Hashed once per process so an unknown email costs the same time as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString("N")));

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    public async Task<LoginOutcome> SignInAsync(string email, string password, string clientIp)
    {
        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            passwordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash.Value, password);
            SecurityLog.LoginFailed(_log, "unknown email", UnknownUser, clientIp);
            return LoginOutcome.Failed;
        }

        // Session cookie only ("remember me" is not offered); lockout counts every failure.
        var result = await signInManager.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            user.LastLoginAt = clock.UtcNow;
            await userManager.UpdateAsync(user);
            SecurityLog.LoginSucceeded(_log, user.Id, clientIp);
            return user.MustChangePassword ? LoginOutcome.MustChangePassword : LoginOutcome.Succeeded;
        }

        if (result.IsLockedOut)
        {
            SecurityLog.LockedOut(_log, user.Id, clientIp);
        }
        else if (result.IsNotAllowed)
        {
            SecurityLog.LoginFailed(_log, user.IsActive ? "not allowed" : "inactive account", user.Id, clientIp);
        }
        else
        {
            SecurityLog.LoginFailed(_log, "wrong password", user.Id, clientIp);
        }

        return LoginOutcome.Failed;
    }

    public async Task SignOutAsync(ClaimsPrincipal principal)
    {
        var userId = userManager.GetUserId(principal);
        await signInManager.SignOutAsync();
        if (userId is not null)
        {
            SecurityLog.LoggedOut(_log, userId);
        }
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null || !user.IsActive)
        {
            return new ChangePasswordResult(false, ["Your session has ended. Please sign in again."]);
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return new ChangePasswordResult(false, ["The new password must be different from the current one."]);
        }

        var changed = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            return new ChangePasswordResult(false, changed.Errors.Select(e => e.Description).ToList());
        }

        user.MustChangePassword = false;
        await userManager.UpdateAsync(user);

        // The password change rotated the security stamp; re-issue this session's cookie with fresh claims.
        await signInManager.RefreshSignInAsync(user);
        SecurityLog.PasswordChanged(_log, user.Id);
        return new ChangePasswordResult(true, []);
    }
}
