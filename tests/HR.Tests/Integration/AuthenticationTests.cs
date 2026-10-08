using System.Net;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Security;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Controllers;
using HR.Web.Security;

namespace HR.Tests.Integration;

public class AuthenticationTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    [Fact]
    public async Task Anonymous_request_redirects_to_login_with_return_url()
    {
        using var client = App.CreateHttpsClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login?ReturnUrl=%2F", response.Headers.Location?.PathAndQueryOrOriginal());
    }

    [Fact]
    public async Task Login_page_is_anonymous_and_uses_the_auth_layout()
    {
        using var client = App.CreateHttpsClient();

        var response = await client.GetAsync("/account/login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("auth-card", html);
        Assert.Contains("<h1 class=\"auth-title\">Sign in</h1>", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
        Assert.DoesNotContain("id=\"appSidebar\"", html);
    }

    [Fact]
    public async Task Valid_login_redirects_home_and_records_last_login()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        using var client = App.CreateHttpsClient();

        var response = await client.PostLoginAsync(user.Email, user.Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        var stored = await App.GetUserAsync(user.Id);
        Assert.NotNull(stored.LastLoginAt);
        Assert.True(DateTimeOffset.UtcNow - stored.LastLoginAt!.Value < TimeSpan.FromMinutes(5) + TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_generic_message()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        using var client = App.CreateHttpsClient();

        var wrongPassword = await client.PostLoginAsync(user.Email, "Wrong-Password-1");
        var unknownEmail = await client.PostLoginAsync("nobody@example.test", "Wrong-Password-1");

        Assert.Equal(HttpStatusCode.OK, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknownEmail.StatusCode);
        var first = AuthHelpers.LoginError(await wrongPassword.Content.ReadAsStringAsync());
        var second = AuthHelpers.LoginError(await unknownEmail.Content.ReadAsStringAsync());
        Assert.Equal(AccountController.GenericLoginError, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Five_failures_lock_the_account_with_the_same_generic_message()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        using var client = App.CreateHttpsClient();

        for (var i = 0; i < 5; i++)
        {
            await client.PostLoginAsync(user.Email, "Wrong-Password-1");
        }

        // Even the correct password is refused while locked, and the message gives nothing away.
        var locked = await client.PostLoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal(AccountController.GenericLoginError, AuthHelpers.LoginError(await locked.Content.ReadAsStringAsync()));

        var stored = await App.GetUserAsync(user.Id);
        Assert.NotNull(stored.LockoutEnd);
        Assert.InRange(stored.LockoutEnd!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(16) + TimeSpan.FromHours(1));
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1002 && e.AllText.Contains(user.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inactive_user_cannot_log_in_and_gets_the_generic_message()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager, isActive: false);
        using var client = App.CreateHttpsClient();

        var response = await client.PostLoginAsync(user.Email, user.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AccountController.GenericLoginError, AuthHelpers.LoginError(await response.Content.ReadAsStringAsync()));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example/path")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    public async Task External_return_urls_are_ignored(string returnUrl)
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        using var client = App.CreateHttpsClient();

        var response = await client.PostLoginAsync(user.Email, user.Password, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Local_return_url_is_followed()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        using var client = App.CreateHttpsClient();

        var response = await client.PostLoginAsync(user.Email, user.Password, "/dev/styleguide");

        Assert.Equal("/dev/styleguide", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Eleventh_login_post_within_a_minute_from_one_ip_gets_429_with_a_friendly_page()
    {
        using var client = App.CreateHttpsClient();
        var token = await client.GetAntiforgeryTokenAsync("/account/login");
        var form = new Dictionary<string, string>
        {
            ["Email"] = "nobody@example.test",
            ["Password"] = "Wrong-Password-1",
            ["__RequestVerificationToken"] = token,
        };

        for (var i = 1; i <= LoginRateLimit.PermitLimit; i++)
        {
            var allowed = await client.PostAsync("/account/login", new FormUrlEncodedContent(form));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var limited = await client.PostAsync("/account/login", new FormUrlEncodedContent(form));
        var html = await limited.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Too many attempts", html);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // A different client IP is not affected.
        using var other = App.CreateHttpsClient();
        var otherToken = await other.GetAntiforgeryTokenAsync("/account/login");
        form["__RequestVerificationToken"] = otherToken;
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync("/account/login", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task Logout_via_get_is_not_allowed()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await client.GetAsync("/account/logout");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode); // still signed in
    }

    [Fact]
    public async Task Logout_post_without_token_is_rejected()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await client.PostAsync("/account/logout", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Logout_post_with_token_signs_out()
    {
        var (client, user) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await client.PostFormAsync("/account/logout", "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1004 && e.AllText.Contains(user.Id, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("short1A", "too short")]
    [InlineData("alllowercase1", "no uppercase")]
    [InlineData("ALLUPPERCASE1", "no lowercase")]
    [InlineData("NoDigitsHere", "no digit")]
    public async Task Weak_passwords_are_rejected_on_change_password(string newPassword, string because)
    {
        var (client, user) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await client.PostFormAsync("/account/change-password", "/account/change-password", new Dictionary<string, string>
        {
            ["CurrentPassword"] = user.Password,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = newPassword,
        });

        Assert.True(response.StatusCode == HttpStatusCode.OK, because);
        Assert.Contains("data-testid=\"change-password-errors\"", await response.Content.ReadAsStringAsync());

        // The old password still works.
        using var fresh = App.CreateHttpsClient();
        Assert.Equal("/", (await fresh.PostLoginAsync(user.Email, user.Password)).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Change_password_requires_the_correct_current_password_and_a_match()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var wrongCurrent = await client.PostFormAsync("/account/change-password", "/account/change-password", new Dictionary<string, string>
        {
            ["CurrentPassword"] = "Not-The-Password-1",
            ["NewPassword"] = "Brand-New-Pass-77",
            ["ConfirmPassword"] = "Brand-New-Pass-77",
        });
        var mismatch = await client.PostFormAsync("/account/change-password", "/account/change-password", new Dictionary<string, string>
        {
            ["CurrentPassword"] = AuthHelpers.DefaultPassword,
            ["NewPassword"] = "Brand-New-Pass-77",
            ["ConfirmPassword"] = "Brand-New-Pass-78",
        });

        Assert.Contains("Incorrect password", await wrongCurrent.Content.ReadAsStringAsync());
        Assert.Contains("The passwords don&#x27;t match", await mismatch.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Change_password_succeeds_keeps_the_session_and_logs_the_event()
    {
        var (client, user) = await App.SignInAsAsync(AppRoles.Manager);

        var response = await client.PostFormAsync("/account/change-password", "/account/change-password", new Dictionary<string, string>
        {
            ["CurrentPassword"] = user.Password,
            ["NewPassword"] = "Brand-New-Pass-77",
            ["ConfirmPassword"] = "Brand-New-Pass-77",
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);

        using var fresh = App.CreateHttpsClient();
        Assert.Equal("/", (await fresh.PostLoginAsync(user.Email, "Brand-New-Pass-77")).Headers.Location?.OriginalString);
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1005 && e.AllText.Contains(user.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("Brand-New-Pass-77", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_events_are_logged_without_passwords()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        using var client = App.CreateHttpsClient();

        await client.PostLoginAsync(user.Email, "Wrong-Secret-Value-9");
        await client.PostLoginAsync(user.Email, user.Password);

        var security = App.Logs.Entries.Where(e => e.Category == SecurityLog.Category).ToList();
        Assert.Contains(security, e => e.EventId.Id == 1001 && e.AllText.Contains(user.Id, StringComparison.Ordinal));
        Assert.Contains(security, e => e.EventId.Id == 1000 && e.AllText.Contains(user.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("Wrong-Secret-Value-9", StringComparison.Ordinal));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains(user.Password, StringComparison.Ordinal));
    }
}

internal static class UriExtensions
{
    public static string PathAndQueryOrOriginal(this Uri uri) => uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
}
