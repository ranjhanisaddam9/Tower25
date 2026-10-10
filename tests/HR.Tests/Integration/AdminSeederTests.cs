using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Security;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HR.Tests.Integration;

public class AdminSeederTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private const string Email = "owner@example.test";
    private const string Password = "Seeded-Admin-Pass-1";

    [Fact]
    public async Task Creates_the_Admin_once_with_a_forced_password_change()
    {
        var logs = new CapturedLogs();

        await SeedAsync(Config(Password), logs);
        await SeedAsync(Config(Password), logs); // idempotent

        await using var scope = Fixture.Development.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admins = await users.GetUsersInRoleAsync(AppRoles.Admin);
        var admin = Assert.Single(admins);
        Assert.Equal(Email, admin.Email);
        Assert.Equal(Email, admin.UserName);
        Assert.Equal("Owner Name", admin.FullName);
        Assert.True(admin.MustChangePassword);
        Assert.True(admin.IsActive);
        Assert.True(await users.CheckPasswordAsync(admin, Password));
        Assert.Single(logs.Entries, e => e.EventId.Id == 1020);
        Assert.DoesNotContain(logs.Entries, e => e.AllText.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Is_a_no_op_when_an_Admin_already_exists()
    {
        var existing = await Fixture.Development.CreateUserAsync(AppRoles.Admin, email: "first.admin@example.test");
        var logs = new CapturedLogs();

        await SeedAsync(Config(Password), logs);

        await using var scope = Fixture.Development.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = Assert.Single(await users.GetUsersInRoleAsync(AppRoles.Admin));
        Assert.Equal(existing.Id, admin.Id);
        Assert.Null(await users.FindByEmailAsync(Email));
        Assert.DoesNotContain(logs.Entries, e => e.Category == SecurityLog.Category);
    }

    [Fact]
    public async Task Skips_with_a_warning_when_the_password_is_missing()
    {
        var logs = new CapturedLogs();

        await SeedAsync(Config(password: null), logs);

        await AssertNoAdminAsync();
        var warning = Assert.Single(logs.Entries, e => e.EventId.Id == 1021);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(AdminSeeder.PasswordKey, warning.Message);
    }

    [Fact]
    public async Task Refuses_a_password_that_comes_from_an_appsettings_file()
    {
        var directory = Directory.CreateTempSubdirectory("hr-seed-test-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "appsettings.SeedTest.json"),
                $$"""{ "Seed": { "Admin": { "Email": "{{Email}}", "FullName": "Owner Name", "Password": "{{Password}}" } } }""");
            var config = new ConfigurationBuilder().SetBasePath(directory.FullName).AddJsonFile("appsettings.SeedTest.json").Build();
            var logs = new CapturedLogs();

            await SeedAsync(config, logs);

            await AssertNoAdminAsync();
            var warning = Assert.Single(logs.Entries, e => e.EventId.Id == 1021);
            Assert.Contains("appsettings", warning.Message);
            Assert.DoesNotContain(Password, warning.AllText);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Creates_both_roles()
    {
        await SeedAsync(Config(password: null), new CapturedLogs());

        await using var scope = Fixture.Development.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        Assert.True(await roles.RoleExistsAsync(AppRoles.Admin));
        Assert.True(await roles.RoleExistsAsync(AppRoles.Manager));
    }

    private static IConfiguration Config(string? password) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AdminSeeder.EmailKey] = Email,
            [AdminSeeder.FullNameKey] = "Owner Name",
            [AdminSeeder.PasswordKey] = password,
        }).Build();

    private async Task SeedAsync(IConfiguration configuration, CapturedLogs logs)
    {
        await using var scope = Fixture.Development.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var seeder = new AdminSeeder(
            services.GetRequiredService<UserManager<ApplicationUser>>(),
            services.GetRequiredService<RoleManager<IdentityRole>>(),
            configuration,
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<HR.Infrastructure.Security.AuditWriter>(),
            loggerFactory);

        await seeder.SeedAsync();
    }

    private async Task AssertNoAdminAsync()
    {
        await using var scope = Fixture.Development.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Empty(await users.GetUsersInRoleAsync(AppRoles.Admin));
    }
}
