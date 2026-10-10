using HR.Domain.Time;
using HR.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Identity;

/// <summary>
/// Startup seeding: makes sure the Admin and Manager roles exist and, when no user holds the Admin role,
/// creates one from Seed:Admin:Email / FullName / Password. Idempotent.
/// The password must come from user secrets or an environment variable, never from a committed appsettings file.
/// </summary>
public sealed class AdminSeeder(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IConfiguration configuration,
    IClock clock,
    AuditWriter audit,
    ILoggerFactory loggerFactory)
{
    public const string EmailKey = "Seed:Admin:Email";
    public const string FullNameKey = "Seed:Admin:FullName";
    public const string PasswordKey = "Seed:Admin:Password";

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AdminSeeder>().SeedAsync(cancellationToken);
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRoleAsync(AppRoles.Admin);
        await EnsureRoleAsync(AppRoles.Manager);

        if ((await userManager.GetUsersInRoleAsync(AppRoles.Admin)).Count > 0)
        {
            return; // An Admin exists: nothing to do.
        }

        var email = configuration[EmailKey]?.Trim();
        var fullName = configuration[FullNameKey]?.Trim();
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName))
        {
            SecurityLog.AdminSeedSkipped(_log, $"{EmailKey} or {FullNameKey} is not configured");
            return;
        }

        if (string.IsNullOrEmpty(password))
        {
            SecurityLog.AdminSeedSkipped(_log, $"{PasswordKey} is not set (use user secrets or an environment variable)");
            return;
        }

        if (PasswordComesFromAppSettingsFile())
        {
            SecurityLog.AdminSeedSkipped(_log, $"{PasswordKey} was found in an appsettings file; it may only come from user secrets or an environment variable");
            await audit.WriteAsync(AuditEvents.AdminSeedSkipped, null, null, null, "Admin seeding refused: the password was found in an appsettings file", cancellationToken);
            return;
        }

        if (await userManager.Users.AnyAsync(u => u.NormalizedEmail == userManager.NormalizeEmail(email), cancellationToken))
        {
            SecurityLog.AdminSeedSkipped(_log, "a user with the configured Admin email already exists but is not an Admin");
            await audit.WriteAsync(AuditEvents.AdminSeedSkipped, null, null, null, "Admin seeding skipped: the configured email belongs to a non-Admin user", cancellationToken);
            return;
        }

        var admin = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = fullName,
            IsActive = true,
            CreatedAt = clock.UtcNow,
            MustChangePassword = true,
        };

        var created = await userManager.CreateAsync(admin, password);
        if (!created.Succeeded)
        {
            // Identity error codes only (e.g. PasswordTooShort); never the password itself.
            SecurityLog.AdminSeedSkipped(_log, "the configured Admin could not be created: " + string.Join(", ", created.Errors.Select(e => e.Code)));
            return;
        }

        var roleAdded = await userManager.AddToRoleAsync(admin, AppRoles.Admin);
        if (!roleAdded.Succeeded)
        {
            await userManager.DeleteAsync(admin);
            SecurityLog.AdminSeedSkipped(_log, "the Admin role could not be assigned: " + string.Join(", ", roleAdded.Errors.Select(e => e.Code)));
            return;
        }

        SecurityLog.AdminSeeded(_log, admin.Id);
        await audit.WriteAsync(AuditEvents.AdminSeeded, null, "User", admin.Id, "Seeded the first Admin from configuration", cancellationToken);
    }

    private async Task EnsureRoleAsync(string role)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            var result = await roleManager.CreateAsync(new IdentityRole(role));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Could not create role '{role}': {string.Join(", ", result.Errors.Select(e => e.Code))}");
            }
        }
    }

    /// <summary>True when any appsettings*.json provider supplies the seed password.</summary>
    private bool PasswordComesFromAppSettingsFile()
    {
        if (configuration is not IConfigurationRoot root)
        {
            return false;
        }

        return root.Providers
            .OfType<FileConfigurationProvider>()
            .Where(p => p.Source.Path?.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) == true)
            .Any(p => p.TryGet(PasswordKey, out var value) && !string.IsNullOrEmpty(value));
    }
}
