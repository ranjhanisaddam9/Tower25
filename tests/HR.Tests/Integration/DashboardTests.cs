using System.Net;
using HR.Domain.Time;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HR.Tests.Integration;

public class DashboardTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    [Fact]
    public async Task Get_root_returns_200_with_sidebar_and_h1()
    {
        using var client = Fixture.Development.CreateHttpsClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"appSidebar\"", html);
        Assert.Contains("<h1 class=\"page-title\">Dashboard</h1>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<h1[\\s>]"));
    }

    [Fact]
    public async Task Sidebar_lists_all_future_sections_as_disabled()
    {
        using var client = Fixture.Development.CreateHttpsClient();

        var html = await client.GetStringAsync("/");

        foreach (var section in new[] { "People", "Absences", "Salaries", "Exchange Rates", "Payroll", "Invoices", "Owner Income", "Reports", "Managers" })
        {
            Assert.Contains($"title=\"{section} (coming soon)\"", html);
        }

        Assert.Contains("aria-current=\"page\"", html);
    }

    [Fact]
    public async Task Dashboard_shows_current_and_next_pay_period_for_today_in_Karachi()
    {
        await using var factory = Fixture.Development.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(new DateOnly(2026, 10, 8)));
            }));
        using var client = factory.CreateHttpsClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("data-testid=\"current-period\">01–15 Oct 2026<", html);
        Assert.Contains("data-testid=\"next-period\">16–31 Oct 2026<", html);
        Assert.Contains("11 working days", html);
        Assert.Contains("08 Oct 2026", html);
    }

    [Fact]
    public async Task Dashboard_rolls_the_next_period_into_the_next_year()
    {
        await using var factory = Fixture.Development.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(new DateOnly(2026, 12, 31)));
            }));
        using var client = factory.CreateHttpsClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("data-testid=\"current-period\">16–31 Dec 2026<", html);
        Assert.Contains("data-testid=\"next-period\">01–15 Jan 2027<", html);
    }

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow => new(today.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero);

        public DateOnly Today => today;
    }
}
