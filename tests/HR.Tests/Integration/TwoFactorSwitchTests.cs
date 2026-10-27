using System.Net;
using HR.Infrastructure.Identity;
using HR.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

/// <summary>
/// The owner's temporary switch (18 Oct 2026): Security:DisableTwoFactorInDevelopment turns two-factor off on the dev PC
/// only. The password is still required, and Production ignores the setting.
/// </summary>
public class TwoFactorSwitchTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private static async Task WithSwitchAsync(HrWebApplicationFactory app, bool on, Func<Task> test)
    {
        var config = app.Services.GetRequiredService<IConfiguration>();
        config[TwoFactorSwitch.SettingKey] = on ? "true" : "false";
        try
        {
            await test();
        }
        finally
        {
            config[TwoFactorSwitch.SettingKey] = "false";
        }
    }

    [Fact]
    public Task In_Development_the_switch_skips_the_code_step_and_the_enrolment_gate_but_not_the_password() =>
        WithSwitchAsync(Fixture.Development, on: true, async () =>
        {
            var app = Fixture.Development;

            // An enrolled Admin: email and password are enough; no code step.
            var enrolled = await app.CreateUserAsync(AppRoles.Admin);
            AuthHelpers.ForgetAuthenticator(enrolled.Email); // the helper must not answer a code step
            var client = app.CreateHttpsClient();
            var login = await client.PostLoginAsync(enrolled.Email, enrolled.Password);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.DoesNotContain("login-2fa", login.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

            // An Admin who never enrolled is not sent to the setup page.
            var notEnrolled = await app.CreateUserAsync(AppRoles.Admin, enrolTwoFactor: false);
            var second = app.CreateHttpsClient();
            await second.PostLoginAsync(notEnrolled.Email, notEnrolled.Password);
            Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/people")).StatusCode);

            // A wrong password still fails.
            var wrong = await app.CreateHttpsClient().PostLoginAsync(enrolled.Email, "Wrong-Password-1");
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode); // the login page again
        });

    [Fact]
    public Task Production_ignores_the_switch() =>
        WithSwitchAsync(Fixture.Production, on: true, async () =>
        {
            var app = Fixture.Production;
            Assert.False(app.Services.GetRequiredService<TwoFactorSwitch>().Disabled);

            var admin = await app.CreateUserAsync(AppRoles.Admin);
            AuthHelpers.ForgetAuthenticator(admin.Email);
            var login = await app.CreateHttpsClient().PostLoginAsync(admin.Email, admin.Password);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.StartsWith("/account/login-2fa", login.Headers.Location!.OriginalString, StringComparison.Ordinal);
        });
}
