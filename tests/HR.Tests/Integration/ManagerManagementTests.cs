using System.Net;
using System.Text.RegularExpressions;
using HR.Infrastructure.Identity;
using HR.Tests.Integration.Infrastructure;

namespace HR.Tests.Integration;

public partial class ManagerManagementTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    [Fact]
    public async Task Manager_gets_403_on_every_admin_route()
    {
        var target = await App.CreateUserAsync(AppRoles.Manager);
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);
        var token = await client.GetAntiforgeryTokenAsync("/account/change-password");

        foreach (var url in new[] { "/admin/managers", "/admin/managers/create", $"/admin/managers/{target.Id}/edit" })
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"GET {url} returned {(int)response.StatusCode}");
            Assert.Contains("Access denied", await response.Content.ReadAsStringAsync());
        }

        var posts = new (string Url, Dictionary<string, string> Fields)[]
        {
            ("/admin/managers/create", new() { ["FullName"] = "Sneaky", ["Email"] = "sneaky@example.test" }),
            ($"/admin/managers/{target.Id}/edit", new() { ["FullName"] = "Renamed", ["Email"] = target.Email }),
            ($"/admin/managers/{target.Id}/deactivate", new()),
            ($"/admin/managers/{target.Id}/activate", new()),
            ($"/admin/managers/{target.Id}/reset-password", new()),
        };
        foreach (var (url, fields) in posts)
        {
            fields["__RequestVerificationToken"] = token;
            var response = await client.PostAsync(url, new FormUrlEncodedContent(fields));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"POST {url} returned {(int)response.StatusCode}");
        }

        var unchanged = await App.GetUserAsync(target.Id);
        Assert.True(unchanged.IsActive);
        Assert.Equal("Test Manager", unchanged.FullName);
    }

    [Fact]
    public async Task Admin_routes_redirect_anonymous_visitors_to_login()
    {
        using var client = App.CreateHttpsClient();

        var response = await client.GetAsync("/admin/managers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/account/login?ReturnUrl=%2Fadmin%2Fmanagers", response.Headers.Location?.PathAndQueryOrOriginal());
    }

    [Fact]
    public async Task List_shows_only_managers_with_empty_state_search_filter_and_paging()
    {
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);

        var empty = await admin.GetStringAsync("/admin/managers");
        Assert.Contains("No managers yet", empty);

        for (var i = 1; i <= 23; i++)
        {
            await App.CreateUserAsync(AppRoles.Manager, email: $"m{i:00}@example.test", fullName: $"Manager {i:00}", isActive: i % 5 != 0);
        }

        await App.CreateUserAsync(AppRoles.Manager, email: "zara@example.test", fullName: "Zara Khan");

        var page1 = await admin.GetStringAsync("/admin/managers");
        Assert.Contains("24 managers", page1);
        Assert.Equal(20, RowRegex().Matches(page1).Count);
        Assert.Contains("Page 1 of 2", page1);
        Assert.DoesNotContain($"class=\"cell-wrap\">{adminUser.Email}<", page1); // Admins are not managed here

        var page2 = await admin.GetStringAsync("/admin/managers?page=2");
        Assert.Equal(4, RowRegex().Matches(page2).Count);

        var byName = await admin.GetStringAsync("/admin/managers?q=zara");
        Assert.Contains("Zara Khan", byName);
        Assert.Contains("1 manager<", byName);

        var byEmail = await admin.GetStringAsync("/admin/managers?q=m07%40example");
        Assert.Contains("Manager 07", byEmail);

        var inactive = await admin.GetStringAsync("/admin/managers?status=Inactive");
        Assert.Contains("4 managers", inactive); // 5, 10, 15, 20
        Assert.Contains("pill pill-danger\">Inactive", inactive);

        var nothing = await admin.GetStringAsync("/admin/managers?q=nobody-matches");
        Assert.Contains("No managers match these filters", nothing);
    }

    [Fact]
    public async Task Create_then_forced_change_then_dashboard()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var created = await admin.PostFormAsync("/admin/managers/create", "/admin/managers/create", new Dictionary<string, string>
        {
            ["FullName"] = "Bilal Ahmed",
            ["Email"] = "bilal@example.test",
        });
        var html = await created.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("no-store", created.Headers.CacheControl?.ToString());
        Assert.Contains("shown only once", html);
        var temporaryPassword = TempPasswordRegex().Match(html).Groups[1].Value;
        Assert.Equal(TemporaryPasswordGenerator.Length, temporaryPassword.Length);

        // Not stored anywhere it could be read back: the list never shows it, and nothing logged it.
        Assert.DoesNotContain(temporaryPassword, await admin.GetStringAsync("/admin/managers"));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains(temporaryPassword, StringComparison.Ordinal));

        // The Manager signs in and is forced to change the password first.
        using var manager = App.CreateHttpsClient();
        var login = await manager.PostLoginAsync("bilal@example.test", temporaryPassword);
        Assert.Equal("/account/change-password", login.Headers.Location?.OriginalString);

        var blocked = await manager.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Equal("/account/change-password", blocked.Headers.Location?.OriginalString);
        Assert.Equal("/account/change-password", (await manager.GetAsync("/dev/styleguide")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/css/glass.css")).StatusCode); // static files still load

        var changePage = await manager.GetStringAsync("/account/change-password");
        Assert.Contains("data-testid=\"forced-change\"", changePage);

        var changed = await manager.PostFormAsync("/account/change-password", "/account/change-password", new Dictionary<string, string>
        {
            ["CurrentPassword"] = temporaryPassword,
            ["NewPassword"] = "Bilal-Own-Pass-2026",
            ["ConfirmPassword"] = "Bilal-Own-Pass-2026",
        });
        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Equal("/", changed.Headers.Location?.OriginalString);

        var dashboard = await manager.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("Your password has been changed.", await dashboard.Content.ReadAsStringAsync());
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains(temporaryPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_email_and_invalid_input()
    {
        var existing = await App.CreateUserAsync(AppRoles.Manager);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var duplicate = await admin.PostFormAsync("/admin/managers/create", "/admin/managers/create", new Dictionary<string, string>
        {
            ["FullName"] = "Someone Else",
            ["Email"] = existing.Email.ToUpperInvariant(),
        });
        var invalid = await admin.PostFormAsync("/admin/managers/create", "/admin/managers/create", new Dictionary<string, string>
        {
            ["FullName"] = "",
            ["Email"] = "not-an-email",
        });

        Assert.Contains("Another user already has this email.", await duplicate.Content.ReadAsStringAsync());
        var invalidHtml = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Enter the full name.", invalidHtml);
        Assert.Contains("Enter a valid email address.", invalidHtml);
    }

    [Fact]
    public async Task Edit_updates_name_and_email_and_keeps_email_unique()
    {
        var target = await App.CreateUserAsync(AppRoles.Manager, fullName: "Hira Malik");
        var other = await App.CreateUserAsync(AppRoles.Manager);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var editUrl = $"/admin/managers/{target.Id}/edit";

        var clash = await admin.PostFormAsync(editUrl, editUrl, new Dictionary<string, string> { ["FullName"] = "Hira Malik", ["Email"] = other.Email });
        Assert.Contains("Another user already has this email.", await clash.Content.ReadAsStringAsync());

        var saved = await admin.PostFormAsync(editUrl, editUrl, new Dictionary<string, string> { ["FullName"] = "Hira Malik-Khan", ["Email"] = "hira.new@example.test" });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        var stored = await App.GetUserAsync(target.Id);
        Assert.Equal("Hira Malik-Khan", stored.FullName);
        Assert.Equal("hira.new@example.test", stored.Email);
        Assert.Equal("hira.new@example.test", stored.UserName);

        using var manager = App.CreateHttpsClient();
        Assert.Equal("/", (await manager.PostLoginAsync("hira.new@example.test", target.Password)).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Edit_of_an_unknown_or_admin_user_is_404()
    {
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/managers/does-not-exist/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/admin/managers/{adminUser.Id}/edit")).StatusCode);
    }

    [Fact]
    public async Task Deactivate_refuses_login_and_rejects_an_open_session_at_the_next_validation()
    {
        var target = await App.CreateUserAsync(AppRoles.Manager);
        using var managerSession = await App.CreateSignedInClientAsync(target);
        Assert.Equal(HttpStatusCode.OK, (await managerSession.GetAsync("/")).StatusCode);

        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        var deactivate = await admin.PostFormAsync($"/admin/managers/{target.Id}/deactivate", "/admin/managers");
        Assert.Equal(HttpStatusCode.Redirect, deactivate.StatusCode);
        Assert.False((await App.GetUserAsync(target.Id)).IsActive);
        Assert.Contains("has been deactivated", await admin.GetStringAsync("/admin/managers"));
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1013 && e.AllText.Contains(adminUser.Id, StringComparison.Ordinal) && e.AllText.Contains(target.Id, StringComparison.Ordinal));

        // New logins are refused.
        using var fresh = App.CreateHttpsClient();
        var login = await fresh.PostLoginAsync(target.Email, target.Password);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // The already-open session dies once the 1-minute validation interval has passed.
        App.Time.Advance(TimeSpan.FromMinutes(2));
        var next = await managerSession.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
        Assert.StartsWith("/account/login", next.Headers.Location?.PathAndQueryOrOriginal());

        // Reactivating lets them back in.
        var activate = await admin.PostFormAsync($"/admin/managers/{target.Id}/activate", "/admin/managers");
        Assert.Equal(HttpStatusCode.Redirect, activate.StatusCode);
        using var again = App.CreateHttpsClient();
        Assert.Equal("/", (await again.PostLoginAsync(target.Email, target.Password)).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_self()
    {
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);

        var response = await admin.PostFormAsync($"/admin/managers/{adminUser.Id}/deactivate", "/admin/managers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True((await App.GetUserAsync(adminUser.Id)).IsActive);
        Assert.Contains("You can&#x27;t deactivate your own account.", await admin.GetStringAsync("/admin/managers"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Reset_password_ends_the_session_and_forces_a_change()
    {
        var target = await App.CreateUserAsync(AppRoles.Manager);
        using var managerSession = await App.CreateSignedInClientAsync(target);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var reset = await admin.PostFormAsync($"/admin/managers/{target.Id}/reset-password", $"/admin/managers/{target.Id}/edit");
        var html = await reset.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("no-store", reset.Headers.CacheControl?.ToString());
        var temporaryPassword = TempPasswordRegex().Match(html).Groups[1].Value;
        Assert.Equal(TemporaryPasswordGenerator.Length, temporaryPassword.Length);
        Assert.True((await App.GetUserAsync(target.Id)).MustChangePassword);

        App.Time.Advance(TimeSpan.FromMinutes(2));
        var next = await managerSession.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
        Assert.StartsWith("/account/login", next.Headers.Location?.PathAndQueryOrOriginal());

        using var fresh = App.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostLoginAsync(target.Email, target.Password)).StatusCode); // old password gone
        Assert.Equal("/account/change-password", (await fresh.PostLoginAsync(target.Email, temporaryPassword)).Headers.Location?.OriginalString);
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains(temporaryPassword, StringComparison.Ordinal));
    }

    [GeneratedRegex("<tr>\\s*<td data-label=\"Name\"")]
    private static partial Regex RowRegex();

    [GeneratedRegex("data-testid=\"temp-password\">([^<]+)<")]
    private static partial Regex TempPasswordRegex();
}
