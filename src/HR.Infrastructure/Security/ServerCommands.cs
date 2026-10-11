using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Security;

/// <summary>
/// Commands run on the server console (M10), never reachable over HTTP:
/// <list type="bullet">
/// <item><c>HR.Web.exe admin-reset --email &lt;e&gt;</c>: emergency Admin recovery.</item>
/// <item><c>HR.Web.exe audit-purge</c>: removes audit rows older than the configured retention (default: keep forever).</item>
/// <item><c>HR.Web.exe seed-admin</c>: creates the roles and, when no Admin exists, the first Admin from <c>Seed:Admin:*</c>
/// (used once by setup.ps1, which passes the password through the process environment only).</item>
/// </list>
/// </summary>
public sealed class ServerCommands(
    AppDbContext db,
    UserManager<ApplicationUser> userManager,
    ITemporaryPasswordGenerator passwordGenerator,
    AdminSeeder adminSeeder,
    IConfiguration configuration,
    IClock clock,
    ILoggerFactory loggerFactory)
{
    public const string AdminReset = "admin-reset";
    public const string AuditPurge = "audit-purge";
    public const string SeedAdmin = "seed-admin";
    public const string RetentionKey = "Audit:RetentionDays";

    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AdminReset, AuditPurge, SeedAdmin };

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    /// <summary>Runs a command line such as ["admin-reset", "--email", "a@b.c"]. Returns the process exit code.</summary>
    public async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken cancellationToken = default)
    {
        var name = args.FirstOrDefault() ?? string.Empty;
        if (string.Equals(name, AdminReset, StringComparison.OrdinalIgnoreCase))
        {
            var emailAt = Array.FindIndex(args, a => string.Equals(a, "--email", StringComparison.OrdinalIgnoreCase));
            if (emailAt < 0 || emailAt + 1 >= args.Length)
            {
                await output.WriteLineAsync("Usage: HR.Web.exe admin-reset --email <admin email>");
                return 2;
            }

            return await AdminResetAsync(args[emailAt + 1], output, cancellationToken);
        }

        if (string.Equals(name, AuditPurge, StringComparison.OrdinalIgnoreCase))
        {
            return await AuditPurgeAsync(output, cancellationToken);
        }

        if (string.Equals(name, SeedAdmin, StringComparison.OrdinalIgnoreCase))
        {
            return await SeedAdminAsync(output, cancellationToken);
        }

        await output.WriteLineAsync($"Unknown command. Available: {string.Join(", ", Names)}");
        return 2;
    }

    /// <summary>
    /// Emergency recovery for an Admin who lost their password or authenticator: a new random password (printed once,
    /// never logged), two-factor and lockout cleared, MustChangePassword set, all sessions ended. Audited. The Admin
    /// signs in, changes the password and enrols a new authenticator (two-factor is compulsory for Admins).
    /// </summary>
    public async Task<int> AdminResetAsync(string email, TextWriter output, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null || !await userManager.IsInRoleAsync(user, AppRoles.Admin))
        {
            await output.WriteLineAsync("No Admin with that email.");
            return 1;
        }

        var password = passwordGenerator.Generate();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Check(await userManager.RemovePasswordAsync(user));
        Check(await userManager.AddPasswordAsync(user, password));
        Check(await userManager.SetTwoFactorEnabledAsync(user, false));
        Check(await userManager.ResetAuthenticatorKeyAsync(user));
        await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        user.MustChangePassword = true;
        user.IsActive = true;
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        db.Audit(AuditEvents.AdminResetCommand, null, "User", user.Id,
            "admin-reset run on the server console: password replaced, two-factor and lockout cleared, sessions ended");
        Check(await userManager.UpdateAsync(user));
        Check(await userManager.UpdateSecurityStampAsync(user));
        await transaction.CommitAsync(cancellationToken);
        SecurityLog.AdminResetCommand(_log, user.Id);

        await output.WriteLineAsync($"Admin {user.Email} has been reset.");
        await output.WriteLineAsync($"Temporary password (shown once, not stored anywhere): {password}");
        await output.WriteLineAsync("Sign in, change the password, then enrol a new authenticator app.");
        return 0;
    }

    /// <summary>
    /// Runs the first-Admin seeding on demand. Exit code 0 when an Admin exists afterwards (created now or already there),
    /// 1 otherwise; the reason is in the log. The password is never printed.
    /// </summary>
    public async Task<int> SeedAdminAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        await adminSeeder.SeedAsync(cancellationToken);
        var admins = await userManager.GetUsersInRoleAsync(AppRoles.Admin);
        if (admins.Count == 0)
        {
            await output.WriteLineAsync("No Admin was created; see the messages above (email, name and a strong password are required).");
            return 1;
        }

        await output.WriteLineAsync($"Admin account ready ({admins.Count} Admin{(admins.Count == 1 ? string.Empty : "s")}).");
        return 0;
    }

    /// <summary>
    /// Deletes audit rows older than <c>Audit:RetentionDays</c>. With no retention configured (the default) nothing is
    /// ever deleted. Needs a SQL login that may ALTER the AuditLog table (to switch the append-only trigger off inside the
    /// transaction); the app's own least-privilege login can't, by design.
    /// </summary>
    public async Task<int> AuditPurgeAsync(TextWriter output, CancellationToken cancellationToken = default)
    {
        var days = configuration.GetValue<int?>(RetentionKey) ?? 0;
        if (days <= 0)
        {
            await output.WriteLineAsync("Audit retention is 'keep forever' (Audit:RetentionDays not set): nothing to purge.");
            return 0;
        }

        var before = clock.UtcNow.AddDays(-days);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"DISABLE TRIGGER [dbo].[{AuditLogConfiguration.AppendOnlyTrigger}] ON [dbo].[{AuditLogConfiguration.Table}]", cancellationToken);
        var removed = await db.AuditLog.Where(a => a.At < before).ExecuteDeleteAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"ENABLE TRIGGER [dbo].[{AuditLogConfiguration.AppendOnlyTrigger}] ON [dbo].[{AuditLogConfiguration.Table}]", cancellationToken);
        db.Audit(AuditEvents.AuditPurged, null, "AuditLog", (object?)null, $"Purged {removed} rows older than {before:yyyy-MM-dd} (retention {days} days)");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        SecurityLog.AuditPurged(_log, removed, before);
        await output.WriteLineAsync($"Removed {removed} audit rows older than {before:yyyy-MM-dd}.");
        return 0;
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Identity update failed: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
