using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Rates;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

public partial class ExchangeRateTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private DateOnly Today => App.Services.GetRequiredService<IClock>().Today;

    private async Task<int> AddRateAsync(DateOnly effectiveFrom, decimal usdToPkr, string? note = null)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ExchangeRateService>()
            .CreateAsync(new RateInput(effectiveFrom, usdToPkr, note), largeChangeConfirmed: true, "test-setup");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors?.Select(e => e.Message) ?? []));
        return result.Id!.Value;
    }

    private static Dictionary<string, string> Form(string effectiveFrom, string rate, string? note = null, bool confirm = false)
    {
        var form = new Dictionary<string, string> { ["EffectiveFrom"] = effectiveFrom, ["UsdToPkr"] = rate };
        if (note is not null) form["Note"] = note;
        if (confirm) form["ConfirmLargeChange"] = "true";
        return form;
    }

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_login()
    {
        var id = await AddRateAsync(new DateOnly(2026, 9, 1), 280m);
        using var client = App.CreateHttpsClient();

        foreach (var url in new[] { "/exchange-rates", "/exchange-rates/create", $"/exchange-rates/{id}/edit" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQueryOrOriginal());
        }
    }

    [Theory]
    [InlineData(AppRoles.Manager)]
    [InlineData(AppRoles.Admin)]
    public async Task Managers_and_Admins_can_list_add_edit_and_delete_rates(string role)
    {
        var (client, _) = await App.SignInAsAsync(role);

        Assert.Contains("No exchange rates yet.", await client.GetStringAsync("/exchange-rates"));

        var created = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-09-01", "280.5", "September"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var list = await client.FollowAsync(created);
        Assert.Contains("added.", list);
        Assert.Contains("data-testid=\"rate-value\">280.50<", list);
        Assert.Contains("Test " + role, list); // added by

        await using var db = TestDatabaseFixture.CreateDbContext();
        var id = await db.ExchangeRates.Where(r => r.EffectiveFrom == new DateOnly(2026, 9, 1)).Select(r => r.Id).SingleAsync();

        var editPage = await client.GetStringAsync($"/exchange-rates/{id}/edit");
        var form = Form("2026-09-01", "281.1234", "Corrected");
        form["RowVersion"] = PeopleHelpers.RowVersion(editPage);
        var edited = await client.PostFormAsync($"/exchange-rates/{id}/edit", $"/exchange-rates/{id}/edit", form);
        Assert.Contains("data-testid=\"rate-value\">281.1234<", await client.FollowAsync(edited));

        var deleted = await client.PostFormAsync($"/exchange-rates/{id}/delete", "/exchange-rates");
        Assert.Contains("deleted.", await client.FollowAsync(deleted));
        await using var check = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(0, await check.ExchangeRates.CountAsync());
    }

    [Fact]
    public async Task Duplicate_date_and_out_of_range_rates_get_friendly_errors()
    {
        await AddRateAsync(new DateOnly(2026, 9, 1), 280m);
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var duplicate = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-09-01", "281"));
        Assert.Contains(WebUtility.HtmlEncode(ExchangeRateService.DuplicateDateMessage), await duplicate.Content.ReadAsStringAsync());

        foreach (var bad in new[] { "99.99", "1000.0001", "5000" })
        {
            var response = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-10-01", bad));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("The rate must be between 100.0000 and 1000.0000 PKR per USD.", await response.Content.ReadAsStringAsync());
        }

        var tooPrecise = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-10-01", "280.12345"));
        Assert.Contains("Use at most 4 decimal places.", await tooPrecise.Content.ReadAsStringAsync());

        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(1, await db.ExchangeRates.CountAsync());
    }

    [Fact]
    public async Task A_change_over_5_percent_needs_the_double_checked_box()
    {
        await AddRateAsync(new DateOnly(2026, 9, 1), 280m);
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        var rejected = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-10-01", "300"));
        var html = await rejected.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("data-testid=\"large-change-warning\"", html);
        Assert.Contains("rise of 7.14%", html);
        Assert.Contains("name=\"ConfirmLargeChange\"", html);
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Equal(1, await db.ExchangeRates.CountAsync());
        }

        // Exactly 5% does not need it.
        var exactly = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-10-01", "294"));
        Assert.Equal(HttpStatusCode.Redirect, exactly.StatusCode);

        // A fall of more than 5% from the entry it follows, confirmed: saved.
        var confirmed = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-11-01", "270", confirm: true));
        Assert.Equal(HttpStatusCode.Redirect, confirmed.StatusCode);

        await using var check = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(3, await check.ExchangeRates.CountAsync());
    }

    [Fact]
    public async Task Second_edit_with_the_same_row_version_gets_the_conflict_message()
    {
        var id = await AddRateAsync(new DateOnly(2026, 9, 1), 280m);
        var (alice, _) = await App.SignInAsAsync(AppRoles.Manager);
        var (bob, _) = await App.SignInAsAsync(AppRoles.Admin);
        var rowVersion = PeopleHelpers.RowVersion(await alice.GetStringAsync($"/exchange-rates/{id}/edit"));

        var aliceForm = Form("2026-09-01", "281");
        aliceForm["RowVersion"] = rowVersion;
        Assert.Equal(HttpStatusCode.Redirect, (await alice.PostFormAsync($"/exchange-rates/{id}/edit", $"/exchange-rates/{id}/edit", aliceForm)).StatusCode);

        var bobForm = Form("2026-09-01", "282");
        bobForm["RowVersion"] = rowVersion;
        var conflict = await bob.PostFormAsync($"/exchange-rates/{id}/edit", $"/exchange-rates/{id}/edit", bobForm);
        var html = await conflict.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains(WebUtility.HtmlEncode(ExchangeRatesController.ConflictMessage), html);
        Assert.Contains("281.00", html);
        Assert.Contains("282.00", html);
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(281m, (await db.ExchangeRates.SingleAsync()).UsdToPkr);
    }

    [Fact]
    public async Task Rate_on_date_lookup_returns_the_right_entry()
    {
        await AddRateAsync(new DateOnly(2026, 9, 1), 278.5m, "September rate");
        await AddRateAsync(new DateOnly(2026, 10, 1), 280.25m, "October rate");
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);

        Assert.Contains("No rate on or before 31 Aug 2026.", Lookup(await client.GetStringAsync("/exchange-rates?on=2026-08-31")));
        var september = Lookup(await client.GetStringAsync("/exchange-rates?on=2026-09-30"));
        Assert.Contains("Rs 278.50", september);
        Assert.Contains("September rate", september);
        Assert.Contains("Rs 280.25", Lookup(await client.GetStringAsync("/exchange-rates?on=2026-10-01")));
    }

    [Fact]
    public async Task Dashboard_tile_shows_the_current_rate_ignores_future_entries_and_says_Not_set_when_empty()
    {
        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);
        var empty = await client.GetStringAsync("/");
        Assert.Matches("data-testid=\"rate-tile\"[\\s\\S]*?stat-value\">Not set<[\\s\\S]*?Add rate", empty);
        Assert.Contains("href=\"/exchange-rates/create\"", empty);

        await AddRateAsync(Today.AddDays(-40), 278.50m);
        await AddRateAsync(Today.AddDays(-5), 280.25m);
        await AddRateAsync(Today.AddDays(10), 290.00m); // scheduled

        var html = await client.GetStringAsync("/");
        Assert.Matches("data-testid=\"rate-tile\"[\\s\\S]*?stat-value\">Rs 280.25<", html);
        Assert.Contains("Up 1.75 (+0.63%) vs previous", WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("Rs 290.00", html);

        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        Assert.Matches("data-testid=\"rate-tile\"[\\s\\S]*?stat-value\">Rs 280.25<", await admin.GetStringAsync("/"));

        var page = await client.GetStringAsync("/exchange-rates");
        Assert.Contains("data-testid=\"current-rate\">1 USD = Rs 280.25<", page);
        Assert.Single(Regex.Matches(page, "data-testid=\"scheduled-pill\""));
    }

    [Fact]
    public async Task Mutations_are_POST_only_and_need_antiforgery()
    {
        var id = await AddRateAsync(new DateOnly(2026, 9, 1), 280m);
        var (client, _) = await App.SignInAsAsync(AppRoles.Admin);

        var get = await client.GetAsync($"/exchange-rates/{id}/delete");
        Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        foreach (var (url, fields) in new (string, Dictionary<string, string>)[]
                 {
                     ("/exchange-rates/create", Form("2026-10-01", "281")),
                     ($"/exchange-rates/{id}/edit", Form("2026-09-01", "282")),
                     ($"/exchange-rates/{id}/delete", new()),
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(url, new FormUrlEncodedContent(fields))).StatusCode);
        }

        await using var db = TestDatabaseFixture.CreateDbContext();
        var rate = await db.ExchangeRates.SingleAsync();
        Assert.Equal(280m, rate.UsdToPkr);
    }

    [Fact]
    public async Task Create_edit_and_delete_are_audited_with_old_and_new_values()
    {
        var (client, user) = await App.SignInAsAsync(AppRoles.Manager);

        var created = await client.PostFormAsync("/exchange-rates/create", "/exchange-rates/create", Form("2026-09-01", "280.5"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        await using var db = TestDatabaseFixture.CreateDbContext();
        var id = await db.ExchangeRates.Select(r => r.Id).SingleAsync();

        var form = Form("2026-09-02", "281.25");
        form["RowVersion"] = PeopleHelpers.RowVersion(await client.GetStringAsync($"/exchange-rates/{id}/edit"));
        await client.PostFormAsync($"/exchange-rates/{id}/edit", $"/exchange-rates/{id}/edit", form);
        await client.PostFormAsync($"/exchange-rates/{id}/delete", "/exchange-rates");

        var added = Assert.Single(App.Logs.Entries, e => e.EventId.Id == 1200 && e.Values.Contains($"RateId={id}"));
        Assert.Contains($"ActorId={user.Id}", added.Values);
        Assert.Contains("NewUsdToPkr=280.5", added.AllText);

        var edited = Assert.Single(App.Logs.Entries, e => e.EventId.Id == 1201 && e.Values.Contains($"RateId={id}"));
        Assert.Contains($"ActorId={user.Id}", edited.Values);
        Assert.Contains("OldUsdToPkr=280.5", edited.AllText);
        Assert.Contains("NewUsdToPkr=281.25", edited.AllText);
        Assert.Contains("->", edited.Message);

        var deleted = Assert.Single(App.Logs.Entries, e => e.EventId.Id == 1202 && e.Values.Contains($"RateId={id}"));
        Assert.Contains("OldUsdToPkr=281.25", deleted.AllText);
    }

    [Fact]
    public async Task Chart_svg_has_no_style_attributes_and_the_page_has_no_inline_script()
    {
        for (var i = 0; i < 14; i++)
        {
            await AddRateAsync(new DateOnly(2025, 9, 1).AddMonths(i), 270m + i);
        }

        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);
        var html = await client.GetStringAsync("/exchange-rates");

        var svg = SvgRegex().Match(html);
        Assert.True(svg.Success, "No chart SVG on the page.");
        Assert.DoesNotContain("style=", svg.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" style=\"", html);
        Assert.Contains("<title id=\"chartTitle\">USD to PKR, last 12 entries</title>", svg.Value);
        Assert.Contains("aria-labelledby=\"chartTitle chartDesc\"", svg.Value);
        Assert.Equal(12, Regex.Matches(svg.Value, "<circle ").Count);
        Assert.Contains("class=\"chart-summary\"", html);
        Assert.Empty(Regex.Matches(html, "<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>", RegexOptions.IgnoreCase));
        Assert.Equal(14, Regex.Matches(html, "data-testid=\"rate-row\"").Count);
    }

    [Fact]
    public async Task History_pages_at_20_entries_newest_first_with_change_vs_previous()
    {
        for (var i = 0; i < 22; i++)
        {
            await AddRateAsync(new DateOnly(2024, 1, 1).AddMonths(i), 250m + i);
        }

        var (client, _) = await App.SignInAsAsync(AppRoles.Manager);
        var page1 = await client.GetStringAsync("/exchange-rates");
        Assert.Equal(20, Regex.Matches(page1, "data-testid=\"rate-row\"").Count);
        Assert.Matches("data-testid=\"rate-value\">271.00<", page1); // newest first
        Assert.Contains("Up 1.00 (+0.37%)", WebUtility.HtmlDecode(page1));

        var page2 = await client.GetStringAsync("/exchange-rates?page=2");
        Assert.Equal(2, Regex.Matches(page2, "data-testid=\"rate-row\"").Count);
        Assert.Contains("First entry", page2);
    }

    private static string Lookup(string html)
    {
        var match = LookupRegex().Match(html);
        Assert.True(match.Success, "No lookup result on the page.");
        return WebUtility.HtmlDecode(Regex.Replace(match.Value, "<[^>]+>", " "));
    }

    [GeneratedRegex("<svg class=\"rate-chart\"[\\s\\S]*?</svg>")]
    private static partial Regex SvgRegex();

    [GeneratedRegex("data-testid=\"lookup-result\">[\\s\\S]*?</div>")]
    private static partial Regex LookupRegex();
}
