using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HR.Tests.Integration;

public class DashboardTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Get_root_returns_200_with_sidebar_and_h1_when_signed_in()
    {
        var (client, _) = await Fixture.Development.SignInAsAsync(AppRoles.Manager);

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"appSidebar\"", html);
        Assert.Contains("<h1 class=\"page-title\">Dashboard</h1>", html);
        Assert.Single(Regex.Matches(html, "<h1[\\s>]"));
    }

    [Fact]
    public async Task Admin_sidebar_links_Managers_and_lists_future_sections_as_disabled()
    {
        var (client, _) = await Fixture.Development.SignInAsAsync(AppRoles.Admin);

        var html = await client.GetStringAsync("/");

        Assert.Contains("href=\"/admin/managers\"", html);
        Assert.Contains("href=\"/absences\"", html);
        Assert.DoesNotContain("title=\"Absences (coming soon)\"", html);
        Assert.Contains("href=\"/payroll\"", html);
        Assert.DoesNotContain("title=\"Payroll (coming soon)\"", html);
        Assert.Contains("href=\"/invoices\"", html);
        Assert.Contains("href=\"/owner-income\"", html);
        Assert.Contains("href=\"/reports\"", html);
        Assert.DoesNotContain("(coming soon)", html); // M9: every section is live

        Assert.Contains("aria-current=\"page\"", html);
    }

    [Fact]
    public async Task Manager_sidebar_does_not_list_Managers()
    {
        var (client, _) = await Fixture.Development.SignInAsAsync(AppRoles.Manager);

        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("/admin/managers", html);
        Assert.DoesNotContain(">Managers<", html);
    }

    [Fact]
    public async Task User_chip_shows_the_real_name_and_role()
    {
        var user = await Fixture.Development.CreateUserAsync(AppRoles.Admin, fullName: "Ayesha Siddiqui");
        using var client = await Fixture.Development.CreateSignedInClientAsync(user);

        var html = await client.GetStringAsync("/");

        Assert.Contains("data-testid=\"user-name\">Ayesha Siddiqui<", html);
        Assert.Contains("data-testid=\"user-role\">Admin<", html);
        Assert.Contains(">AS<", html); // initials avatar
    }

    [Fact]
    public async Task Dashboard_shows_current_and_next_pay_period_for_today_in_Karachi()
    {
        await using var factory = WithClock(new DateOnly(2026, 10, 8));
        var user = await Fixture.Development.CreateUserAsync(AppRoles.Manager);
        using var client = factory.CreateHttpsClient();
        await client.PostLoginAsync(user.Email, user.Password);

        var html = await client.GetStringAsync("/");

        Assert.Contains("data-testid=\"current-period\">01–15 Oct 2026<", html);
        Assert.Contains("data-testid=\"next-period\">16–31 Oct 2026<", html);
        Assert.Contains("11 working days", html);
        Assert.Contains("08 Oct 2026", html);
    }

    [Fact]
    public async Task Dashboard_rolls_the_next_period_into_the_next_year()
    {
        await using var factory = WithClock(new DateOnly(2026, 12, 31));
        var user = await Fixture.Development.CreateUserAsync(AppRoles.Manager);
        using var client = factory.CreateHttpsClient();
        await client.PostLoginAsync(user.Email, user.Password);

        var html = await client.GetStringAsync("/");

        Assert.Contains("data-testid=\"current-period\">16–31 Dec 2026<", html);
        Assert.Contains("data-testid=\"next-period\">01–15 Jan 2027<", html);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithClock(DateOnly today) =>
        Fixture.Development.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(today));
            }));

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero);

        public DateOnly Today => today;
    }
}
