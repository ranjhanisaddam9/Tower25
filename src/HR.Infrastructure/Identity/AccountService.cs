using System.Globalization;
using System.Security.Claims;
using System.Text;
using HR.Domain.Time;
using HR.Infrastructure.Data;
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

    /// <summary>The password was right; the authenticator code (or a recovery code) is needed next.</summary>
    RequiresTwoFactor,

    /// <summary>Not signed in. Callers show one generic message whatever the reason.</summary>
    Failed,
}

public sealed record ChangePasswordResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>What the enrolment page shows: the key to type in by hand and the otpauth URI the QR code encodes.</summary>
public sealed record AuthenticatorSetup(string FormattedKey, string Uri);

public sealed record TwoFactorStatus(bool Enabled, bool Required, int RecoveryCodesLeft);

/// <summary>
/// Sign-in (password, then authenticator or recovery code), sign-out, password change and two-factor enrolment for the
/// signed-in user, with security events logged and written to the audit trail.
/// </summary>
public sealed class AccountService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IPasswordHasher<ApplicationUser> passwordHasher,
    AppDbContext db,
    AuditWriter audit,
    IClock clock,
    ILoggerFactory loggerFactory)
{
    public const string Issuer = "HR Payroll";
    public const int RecoveryCodeCount = 10;

    private const string UnknownUser = "(unknown)";

    // Hashed once per process so an unknown email costs the same time as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString("N")));

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    // ===================== Sign-in =====================

    public async Task<LoginOutcome> SignInAsync(string email, string password, string clientIp)
    {
        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            passwordHasher.VerifyHashedPassword(new ApplicationUser(), DummyHash.Value, password);
            SecurityLog.LoginFailed(_log, "unknown email", UnknownUser, clientIp);
            await audit.WriteAsync(AuditEvents.LoginFailed, null, null, null, "Login failed: unknown email");
            return LoginOutcome.Failed;
        }

        // Session cookie only ("remember me" is not offered); lockout counts every failure. A fresh cookie is issued on
        // success (Identity never reuses a pre-login cookie), so a planted session id can't be fixed in place.
        var result = await signInManager.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
        {
            return LoginOutcome.RequiresTwoFactor;
        }

        if (result.Succeeded)
        {
            return await CompleteSignInAsync(user, clientIp, "password");
        }

        await AuditFailureAsync(user, result, clientIp, "wrong password");
        return LoginOutcome.Failed;
    }

    /// <summary>The second step: a 6-digit authenticator code. Wrong codes count towards lockout.</summary>
    public async Task<LoginOutcome> TwoFactorSignInAsync(string code, string clientIp)
    {
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return LoginOutcome.Failed; // the password step expired or never happened
        }

        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(NormalizeCode(code), isPersistent: false, rememberClient: false);
        if (result.Succeeded)
        {
            return await CompleteSignInAsync(user, clientIp, "password + authenticator");
        }

        await AuditFailureAsync(user, result, clientIp, "wrong authenticator code");
        return LoginOutcome.Failed;
    }

    /// <summary>The second step with a one-time recovery code. Each code works once; wrong codes count towards lockout.</summary>
    public async Task<LoginOutcome> RecoveryCodeSignInAsync(string code, string clientIp)
    {
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null || await userManager.IsLockedOutAsync(user))
        {
            return LoginOutcome.Failed;
        }

        var result = await signInManager.TwoFactorRecoveryCodeSignInAsync(code.Replace(" ", string.Empty, StringComparison.Ordinal).Trim());
        if (result.Succeeded)
        {
            var left = await userManager.CountRecoveryCodesAsync(user);
            SecurityLog.RecoveryCodeUsed(_log, user.Id, left);
            db.Audit(AuditEvents.RecoveryCodeUsed, user.Id, "User", user.Id, $"Signed in with a recovery code; {left} left");
            return await CompleteSignInAsync(user, clientIp, "password + recovery code");
        }

        // Identity does not count recovery-code failures; this app does (same lockout as the password and the code).
        await userManager.AccessFailedAsync(user);
        await AuditFailureAsync(user, await userManager.IsLockedOutAsync(user) ? SignInResult.LockedOut : SignInResult.Failed, clientIp, "wrong recovery code");
        return LoginOutcome.Failed;
    }

    private async Task<LoginOutcome> CompleteSignInAsync(ApplicationUser user, string clientIp, string method)
    {
        user.LastLoginAt = clock.UtcNow;
        db.Audit(AuditEvents.LoginSucceeded, user.Id, "User", user.Id, $"Signed in ({method})");
        await userManager.UpdateAsync(user); // saves the audit row in the same transaction
        SecurityLog.LoginSucceeded(_log, user.Id, clientIp);
        return user.MustChangePassword ? LoginOutcome.MustChangePassword : LoginOutcome.Succeeded;
    }

    private async Task AuditFailureAsync(ApplicationUser user, SignInResult result, string clientIp, string reason)
    {
        if (result.IsLockedOut)
        {
            SecurityLog.LockedOut(_log, user.Id, clientIp);
            await audit.WriteAsync(AuditEvents.LockedOut, user.Id, "User", user.Id, $"Locked out after a {reason}");
        }
        else if (result.IsNotAllowed)
        {
            SecurityLog.LoginFailed(_log, user.IsActive ? "not allowed" : "inactive account", user.Id, clientIp);
            await audit.WriteAsync(AuditEvents.LoginFailed, user.Id, "User", user.Id, user.IsActive ? "Login failed: not allowed" : "Login failed: inactive account");
        }
        else if (reason.Contains("code", StringComparison.Ordinal))
        {
            SecurityLog.TwoFactorFailed(_log, user.Id, clientIp);
            await audit.WriteAsync(AuditEvents.TwoFactorFailed, user.Id, "User", user.Id, $"Two-factor step failed: {reason}");
        }
        else
        {
            SecurityLog.LoginFailed(_log, reason, user.Id, clientIp);
            await audit.WriteAsync(AuditEvents.LoginFailed, user.Id, "User", user.Id, $"Login failed: {reason}");
        }
    }

    /// <summary>
    /// Signs out and rotates the security stamp: every other session of this user (other browsers, devices) ends at its
    /// next cookie validation.
    /// </summary>
    public async Task SignOutAsync(ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is not null)
        {
            db.Audit(AuditEvents.LoggedOut, user.Id, "User", user.Id, "Signed out; all sessions ended");
            await userManager.UpdateSecurityStampAsync(user);
        }

        await signInManager.SignOutAsync();
        if (user is not null)
        {
            SecurityLog.LoggedOut(_log, user.Id);
        }
    }

    // ===================== Password =====================

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
        db.Audit(AuditEvents.PasswordChanged, user.Id, "User", user.Id, "Changed their password");
        await userManager.UpdateAsync(user);

        // The password change rotated the security stamp; re-issue this session's cookie with fresh claims.
        await signInManager.RefreshSignInAsync(user);
        SecurityLog.PasswordChanged(_log, user.Id);
        return new ChangePasswordResult(true, []);
    }

    // ===================== Two-factor enrolment =====================

    public static bool IsTwoFactorRequired(bool isAdmin, ApplicationUser user) => isAdmin || user.RequireTwoFactor;

    public async Task<TwoFactorStatus?> GetTwoFactorStatusAsync(ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return null;
        }

        var required = IsTwoFactorRequired(await userManager.IsInRoleAsync(user, AppRoles.Admin), user);
        return new TwoFactorStatus(user.TwoFactorEnabled, required, user.TwoFactorEnabled ? await userManager.CountRecoveryCodesAsync(user) : 0);
    }

    /// <summary>The authenticator key for enrolment (created on first use; kept until enrolment completes or is reset).</summary>
    public async Task<AuthenticatorSetup?> GetAuthenticatorSetupAsync(ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return null;
        }

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        return new AuthenticatorSetup(FormatKey(key!), AuthenticatorUri(user.Email!, key!));
    }

    /// <summary>Checks a first code from the new authenticator, turns two-factor on and returns 10 recovery codes (shown once).</summary>
    public async Task<IReadOnlyList<string>?> EnableTwoFactorAsync(ClaimsPrincipal principal, string code)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return null;
        }

        var valid = await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(code));
        if (!valid)
        {
            return null;
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var codes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))!.ToList();
        db.Audit(AuditEvents.TwoFactorEnabled, user.Id, "User", user.Id, "Enrolled an authenticator app");
        db.Audit(AuditEvents.RecoveryCodesGenerated, user.Id, "User", user.Id, $"{RecoveryCodeCount} recovery codes generated");
        await userManager.UpdateAsync(user);
        await signInManager.RefreshSignInAsync(user); // fresh claims: the enrolment gate opens
        SecurityLog.TwoFactorEnabled(_log, user.Id);
        return codes;
    }

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is not { TwoFactorEnabled: true })
        {
            return null;
        }

        var codes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))!.ToList();
        db.Audit(AuditEvents.RecoveryCodesGenerated, user.Id, "User", user.Id, $"{RecoveryCodeCount} new recovery codes generated (old ones no longer work)");
        await userManager.UpdateAsync(user);
        SecurityLog.RecoveryCodesGenerated(_log, user.Id);
        return codes;
    }

    /// <summary>Turns two-factor off for a user who is not required to have it. Refused when it is required.</summary>
    public async Task<bool> DisableTwoFactorAsync(ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null || IsTwoFactorRequired(await userManager.IsInRoleAsync(user, AppRoles.Admin), user))
        {
            return false;
        }

        await userManager.SetTwoFactorEnabledAsync(user, false);
        await userManager.ResetAuthenticatorKeyAsync(user);
        db.Audit(AuditEvents.TwoFactorDisabled, user.Id, "User", user.Id, "Turned two-factor sign-in off");
        await userManager.UpdateAsync(user);
        await signInManager.RefreshSignInAsync(user);
        SecurityLog.TwoFactorDisabled(_log, user.Id, user.Id);
        return true;
    }

    /// <summary>Digits only: authenticator apps show "123 456"; people type "123-456".</summary>
    public static string NormalizeCode(string? code) => new((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    /// <summary>"ABCD EFGH IJKL …" in lower case for easier typing (Base32 is case-insensitive).</summary>
    public static string FormatKey(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            builder.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        }

        return builder.ToString().TrimEnd().ToLowerInvariant();
    }

    public static string AuthenticatorUri(string email, string key) =>
        string.Format(CultureInfo.InvariantCulture, "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            Uri.EscapeDataString(Issuer), Uri.EscapeDataString(email), key);
}
