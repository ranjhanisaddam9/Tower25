using System.Net;
using System.Text.RegularExpressions;
using HR.Infrastructure.Data;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Security;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

/// <summary>M10: an authenticator code works once. Replays and older codes are refused at sign-in and at enrolment.</summary>
public class TotpReplayTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private const string TwoFactorPage = "/account/login-2fa";
    private const string SetupPage = "/account/two-factor/setup";

    private static string CodeAt(string key, long step) => Totp.Code(key, DateTimeOffset.FromUnixTimeSeconds(step * 30));

    private long CurrentStep => App.Time.GetUtcNow().ToUnixTimeSeconds() / 30; // the test host's clock

    private async Task<HttpClient> PasswordStepAsync(TestUser user)
    {
        AuthHelpers.ForgetAuthenticator(user.Email); // answer the code step by hand
        var client = App.CreateHttpsClient();
        var step = await client.PostLoginAsync(user.Email, user.Password);
        Assert.StartsWith(TwoFactorPage, step.Headers.Location!.OriginalString, StringComparison.Ordinal);
        return client;
    }

    [Fact]
    public async Task A_code_that_signed_in_once_is_refused_the_second_time()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin);
        var code = CodeAt(user.AuthenticatorKey!, CurrentStep);

        var first = await PasswordStepAsync(user);
        Assert.Equal(HttpStatusCode.Redirect, (await first.PostTwoFactorCodeAsync(code)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/")).StatusCode);

        // Someone who saw the code (shoulder-surfing, a phishing proxy) replays it in another browser.
        var replay = await PasswordStepAsync(user);
        var refused = await replay.PostTwoFactorCodeAsync(code);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode); // the code page again
        Assert.Contains("That code didn", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await replay.GetAsync("/")).StatusCode);
        Assert.True(await AuditedAsync(AuditEvents.TwoFactorFailed, user.Id));

        // The next code from the app still works.
        Assert.Equal(HttpStatusCode.Redirect, (await replay.PostTwoFactorCodeAsync(CodeAt(user.AuthenticatorKey!, CurrentStep + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await replay.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task A_code_older_than_the_last_accepted_one_is_refused_even_inside_the_drift_window()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin);
        var now = CurrentStep;

        var client = await PasswordStepAsync(user);
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostTwoFactorCodeAsync(CodeAt(user.AuthenticatorKey!, now + 1))).StatusCode);
        Assert.Equal(now + 1, (await App.GetUserAsync(user.Id)).LastTotpTimeStep);

        var other = await PasswordStepAsync(user);
        foreach (var older in new[] { now - 1, now, now + 1 })
        {
            Assert.Equal(HttpStatusCode.OK, (await other.PostTwoFactorCodeAsync(CodeAt(user.AuthenticatorKey!, older))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Redirect, (await other.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task The_enrolment_code_cannot_be_replayed_at_sign_in()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin, enrolTwoFactor: false);
        var client = App.CreateHttpsClient();
        await client.PostLoginAsync(user.Email, user.Password);
        var setup = await client.GetStringAsync(SetupPage);
        var key = Regex.Match(setup, "data-testid=\"manual-key\">([a-z0-9 ]+)<").Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var code = CodeAt(key, CurrentStep);

        var enrolled = await client.PostFormAsync(SetupPage, SetupPage, new Dictionary<string, string> { ["Code"] = code });
        Assert.Contains("data-testid=\"recovery-code\"", await enrolled.Content.ReadAsStringAsync());

        var replay = await PasswordStepAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await replay.PostTwoFactorCodeAsync(code)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await replay.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task A_used_code_cannot_confirm_a_new_enrolment()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager, enrolTwoFactor: false);
        await using var scope = App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var entity = (await users.FindByIdAsync(user.Id))!;
        await users.ResetAuthenticatorKeyAsync(entity);
        var key = (await users.GetAuthenticatorKeyAsync(entity))!;
        var code = CodeAt(key, CurrentStep);
        var provider = users.Options.Tokens.AuthenticatorTokenProvider;

        Assert.True(await users.VerifyTwoFactorTokenAsync(entity, provider, code));
        Assert.False(await users.VerifyTwoFactorTokenAsync(entity, provider, code));
    }

    [Fact]
    public async Task Two_requests_racing_with_the_same_code_cannot_both_succeed()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin);
        var code = CodeAt(user.AuthenticatorKey!, CurrentStep);

        async Task<bool> VerifyInOwnScopeAsync()
        {
            await using var scope = App.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var entity = (await users.FindByIdAsync(user.Id))!;
            return await users.VerifyTwoFactorTokenAsync(entity, users.Options.Tokens.AuthenticatorTokenProvider, code);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(VerifyInOwnScopeAsync)));
        Assert.Equal(1, results.Count(ok => ok));
    }

    private async Task<bool> AuditedAsync(AuditEvent auditEvent, string userId)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLog.AnyAsync(a => a.EventId == auditEvent.Id && a.ActorUserId == userId);
    }
}
