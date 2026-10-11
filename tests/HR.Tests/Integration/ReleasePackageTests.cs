using System.Net;
using HR.Infrastructure.Backups;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Security;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

/// <summary>M10 release package: the dashboard backup warning and the seed-admin server command.</summary>
public class ReleasePackageTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private async Task WithStatusFileAsync(string? json, Func<Task> test)
    {
        var dir = Directory.CreateTempSubdirectory("hr-backup-").FullName;
        var path = Path.Combine(dir, "status.json");
        if (json is not null)
        {
            await File.WriteAllTextAsync(path, json);
        }

        var config = App.Services.GetRequiredService<IConfiguration>();
        config[BackupStatusService.StatusFileKey] = path;
        try
        {
            await test();
        }
        finally
        {
            config[BackupStatusService.StatusFileKey] = string.Empty;
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Status(DateTimeOffset lastRun, bool ok, DateTimeOffset? lastGood, string message) =>
        $$"""{ "lastRunUtc": "{{lastRun:o}}", "lastRunOk": {{(ok ? "true" : "false")}}, "lastGoodUtc": {{(lastGood is { } g ? $"\"{g:o}\"" : "null")}}, "message": "{{message}}" }""";

    [Fact]
    public async Task Without_a_configured_status_file_there_is_no_backup_warning()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        Assert.DoesNotContain("data-testid=\"backup-alert\"", await admin.GetStringAsync("/"));
    }

    [Fact]
    public async Task The_Admin_dashboard_warns_when_the_last_backup_failed_and_Managers_never_see_it()
    {
        var now = DateTimeOffset.UtcNow;
        await WithStatusFileAsync(Status(now, ok: false, now.AddHours(-30), "RESTORE VERIFYONLY failed <script>"), async () =>
        {
            var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
            var html = await admin.GetStringAsync("/");
            Assert.Contains("data-testid=\"backup-alert\"", html);
            Assert.Contains("The last backup failed", html);
            Assert.Contains("RESTORE VERIFYONLY failed &lt;script&gt;", html); // encoded like any text

            var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
            Assert.DoesNotContain("backup-alert", await manager.GetStringAsync("/"));
        });
    }

    [Fact]
    public async Task The_Admin_dashboard_warns_when_the_last_good_backup_is_older_than_48_hours_or_missing()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var now = DateTimeOffset.UtcNow;

        await WithStatusFileAsync(Status(now.AddHours(-50), ok: true, now.AddHours(-50), "OK"), async () =>
            Assert.Contains("more than 48 hours old", await admin.GetStringAsync("/")));

        await WithStatusFileAsync(null, async () =>
            Assert.Contains("No backup has run yet", await admin.GetStringAsync("/")));

        await WithStatusFileAsync("not json", async () =>
            Assert.Contains("backup status can", await admin.GetStringAsync("/")));

        await WithStatusFileAsync(Status(now.AddHours(-3), ok: true, now.AddHours(-3), "OK"), async () =>
            Assert.DoesNotContain("data-testid=\"backup-alert\"", await admin.GetStringAsync("/")));
    }

    [Fact]
    public async Task Seed_admin_creates_the_first_Admin_once_from_the_process_settings_and_never_prints_the_password()
    {
        const string password = "Package-Test-Pass-7";
        var seeded = App.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminSeeder.EmailKey] = "owner@package.test",
            [AdminSeeder.FullNameKey] = "Package Owner",
            [AdminSeeder.PasswordKey] = password,
        })));

        // Without the settings nothing is created and the command says so.
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var output = new StringWriter();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ServerCommands>().RunAsync([ServerCommands.SeedAdmin], output));
        }

        for (var run = 0; run < 2; run++) // idempotent: the second run changes nothing
        {
            await using var scope = seeded.Services.CreateAsyncScope();
            var output = new StringWriter();
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ServerCommands>().RunAsync([ServerCommands.SeedAdmin], output));
            Assert.DoesNotContain(password, output.ToString());
        }

        await using (var scope = App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admins = await users.GetUsersInRoleAsync(AppRoles.Admin);
            var admin = Assert.Single(admins);
            Assert.Equal("owner@package.test", admin.Email);
            Assert.True(admin.MustChangePassword);
        }

        // The new Admin signs in, must change the password, then must enrol in two-factor.
        var client = App.CreateHttpsClient();
        var login = await client.PostLoginAsync("owner@package.test", password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("change-password", login.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
    }
}
