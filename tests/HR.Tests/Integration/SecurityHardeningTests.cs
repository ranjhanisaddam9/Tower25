using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Settings;
using HR.Infrastructure.Data;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Security;
using HR.Infrastructure.Settings;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace HR.Tests.Integration;

/// <summary>M10: authorization matrix, antiforgery, XSS, cookies and headers, two-factor, sessions, audit trail, limits, failures.</summary>
public partial class SecurityHardeningTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    // ===================== Authorization matrix =====================

    private enum Access
    {
        /// <summary>[AllowAnonymous]: login, two-factor step, error pages.</summary>
        Anonymous,

        /// <summary>The fallback policy: any signed-in user.</summary>
        SignedIn,

        ManagerOrAdmin,

        AdminOnly,
    }

    /// <summary>
    /// Every routed action, reviewed. A new endpoint without an entry fails <see cref="Every_routed_action_is_in_the_reviewed_authorization_matrix"/>,
    /// so nothing can be added without deciding who may call it.
    /// </summary>
    private static readonly Dictionary<string, Access> Matrix = new()
    {
        ["ANY /error/{statusCode:int}"] = Access.Anonymous,
        ["GET /{controller=Home}/{action=Index}/{id?}"] = Access.SignedIn,
        ["GET /absences"] = Access.ManagerOrAdmin,
        ["GET /absences/{id:int}/edit"] = Access.ManagerOrAdmin,
        ["GET /absences/day"] = Access.ManagerOrAdmin,
        ["GET /absences/export"] = Access.ManagerOrAdmin,
        ["GET /absences/new"] = Access.ManagerOrAdmin,
        ["GET /absences/range"] = Access.ManagerOrAdmin,
        ["GET /account/change-password"] = Access.SignedIn,
        ["GET /account/login-2fa"] = Access.Anonymous,
        ["GET /account/login-recovery"] = Access.Anonymous,
        ["GET /account/login"] = Access.Anonymous,
        ["GET /account/logout"] = Access.SignedIn,
        ["GET /account/security"] = Access.SignedIn,
        ["GET /account/two-factor/setup"] = Access.SignedIn,
        ["GET /admin/audit"] = Access.AdminOnly,
        ["GET /admin/audit/export"] = Access.AdminOnly,
        ["GET /admin/managers"] = Access.AdminOnly,
        ["GET /admin/managers/{id}/edit"] = Access.AdminOnly,
        ["GET /admin/managers/create"] = Access.AdminOnly,
        ["GET /admin/settings"] = Access.AdminOnly,
        ["GET /dev/styleguide"] = Access.SignedIn,
        ["GET /exchange-rates"] = Access.ManagerOrAdmin,
        ["GET /exchange-rates/{id:int}/edit"] = Access.ManagerOrAdmin,
        ["GET /exchange-rates/create"] = Access.ManagerOrAdmin,
        ["GET /exchange-rates/export"] = Access.ManagerOrAdmin,
        ["GET /invoices"] = Access.AdminOnly,
        ["GET /invoices/{id:int}"] = Access.AdminOnly,
        ["GET /invoices/{id:int}/export"] = Access.AdminOnly,
        ["GET /invoices/{id:int}/pdf"] = Access.AdminOnly,
        ["GET /owner-income"] = Access.AdminOnly,
        ["GET /owner-income/export"] = Access.AdminOnly,
        ["GET /payroll"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/export"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/lines/{lineId:int}"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/lines/{lineId:int}/adjustments/{adjustmentId:int}/edit"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/lines/{lineId:int}/payslip"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/lines/{lineId:int}/payslip/pdf"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/payslips"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/payslips/pdf"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/register"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/register/export"] = Access.ManagerOrAdmin,
        ["GET /payroll/{id:int}/register/pdf"] = Access.ManagerOrAdmin,
        ["GET /people"] = Access.ManagerOrAdmin,
        ["GET /people/{id:int}"] = Access.ManagerOrAdmin,
        ["GET /people/{id:int}/edit"] = Access.ManagerOrAdmin,
        ["GET /people/{personId:int}/pay/{recordId:int}/edit"] = Access.AdminOnly,
        ["GET /people/{personId:int}/pay/{recordId:int}/increment-edit"] = Access.ManagerOrAdmin,
        ["GET /people/{personId:int}/pay/increment"] = Access.ManagerOrAdmin,
        ["GET /people/{personId:int}/pay/new"] = Access.AdminOnly,
        ["GET /people/create"] = Access.ManagerOrAdmin,
        ["GET /people/export"] = Access.ManagerOrAdmin,
        ["GET /reports"] = Access.ManagerOrAdmin,
        ["GET /reports/absences"] = Access.ManagerOrAdmin,
        ["GET /reports/absences/export"] = Access.ManagerOrAdmin,
        ["GET /reports/headcount"] = Access.ManagerOrAdmin,
        ["GET /reports/headcount/export"] = Access.ManagerOrAdmin,
        ["GET /reports/payroll-history"] = Access.ManagerOrAdmin,
        ["GET /reports/payroll-history/export"] = Access.ManagerOrAdmin,
        ["GET /reports/salary-changes"] = Access.ManagerOrAdmin,
        ["GET /reports/salary-changes/export"] = Access.ManagerOrAdmin,
        ["GET /salaries"] = Access.ManagerOrAdmin,
        ["GET /salaries/export"] = Access.ManagerOrAdmin,
        ["POST /absences/{id:int}/delete"] = Access.ManagerOrAdmin,
        ["POST /absences/{id:int}/edit"] = Access.ManagerOrAdmin,
        ["POST /absences/day"] = Access.ManagerOrAdmin,
        ["POST /absences/new"] = Access.ManagerOrAdmin,
        ["POST /absences/range"] = Access.ManagerOrAdmin,
        ["POST /account/change-password"] = Access.SignedIn,
        ["POST /account/login-2fa"] = Access.Anonymous,
        ["POST /account/login-recovery"] = Access.Anonymous,
        ["POST /account/login"] = Access.Anonymous,
        ["POST /account/logout"] = Access.SignedIn,
        ["POST /account/two-factor/disable"] = Access.SignedIn,
        ["POST /account/two-factor/recovery-codes"] = Access.SignedIn,
        ["POST /account/two-factor/setup"] = Access.SignedIn,
        ["POST /admin/managers/{id}/activate"] = Access.AdminOnly,
        ["POST /admin/managers/{id}/deactivate"] = Access.AdminOnly,
        ["POST /admin/managers/{id}/edit"] = Access.AdminOnly,
        ["POST /admin/managers/{id}/reset-password"] = Access.AdminOnly,
        ["POST /admin/managers/{id}/two-factor/require"] = Access.AdminOnly,
        ["POST /admin/managers/{id}/two-factor/reset"] = Access.AdminOnly,
        ["POST /admin/managers/create"] = Access.AdminOnly,
        ["POST /admin/settings"] = Access.AdminOnly,
        ["POST /dev/styleguide/toast"] = Access.SignedIn,
        ["POST /exchange-rates/{id:int}/delete"] = Access.ManagerOrAdmin,
        ["POST /exchange-rates/{id:int}/edit"] = Access.ManagerOrAdmin,
        ["POST /exchange-rates/create"] = Access.ManagerOrAdmin,
        ["POST /invoices/{id:int}/paid"] = Access.AdminOnly,
        ["POST /invoices/{id:int}/unpaid"] = Access.AdminOnly,
        ["POST /invoices/issue/{runId:int}"] = Access.AdminOnly,
        ["POST /payroll/{id:int}/delete"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/finalize"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/lines/{lineId:int}/adjustments"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/lines/{lineId:int}/adjustments/{adjustmentId:int}/delete"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/lines/{lineId:int}/adjustments/{adjustmentId:int}/edit"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/lines/{lineId:int}/extra-days"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/rate"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/regenerate"] = Access.ManagerOrAdmin,
        ["POST /payroll/{id:int}/reopen"] = Access.AdminOnly,
        ["POST /payroll/generate"] = Access.ManagerOrAdmin,
        ["POST /people/{id:int}/cancel-leaving"] = Access.ManagerOrAdmin,
        ["POST /people/{id:int}/deactivate"] = Access.ManagerOrAdmin,
        ["POST /people/{id:int}/edit"] = Access.ManagerOrAdmin,
        ["POST /people/{id:int}/hire-source"] = Access.AdminOnly,
        ["POST /people/{id:int}/reactivate"] = Access.ManagerOrAdmin,
        ["POST /people/{personId:int}/pay/{recordId:int}/delete"] = Access.ManagerOrAdmin,
        ["POST /people/{personId:int}/pay/{recordId:int}/edit"] = Access.AdminOnly,
        ["POST /people/{personId:int}/pay/{recordId:int}/increment-edit"] = Access.ManagerOrAdmin,
        ["POST /people/{personId:int}/pay/{recordId:int}/reviewed"] = Access.AdminOnly,
        ["POST /people/{personId:int}/pay/increment"] = Access.ManagerOrAdmin,
        ["POST /people/{personId:int}/pay/new"] = Access.AdminOnly,
        ["POST /people/create"] = Access.ManagerOrAdmin,
    };

    private sealed record RoutedAction(string Key, string Method, string Template, Endpoint Endpoint);

    private List<RoutedAction> RoutedActions() =>
        App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .Select(e =>
            {
                var method = string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"]);
                return new RoutedAction($"{method} /{e.RoutePattern.RawText}", method, "/" + e.RoutePattern.RawText, e);
            })
            .ToList();

    [Fact]
    public void Every_routed_action_is_in_the_reviewed_authorization_matrix()
    {
        var actions = RoutedActions();
        var unreviewed = actions.Select(a => a.Key).Except(Matrix.Keys).ToList();
        var stale = Matrix.Keys.Except(actions.Select(a => a.Key)).ToList();
        Assert.True(unreviewed.Count == 0, "Endpoints without an authorization-matrix entry (review them, then add them): " + string.Join("; ", unreviewed));
        Assert.True(stale.Count == 0, "Matrix entries for endpoints that no longer exist: " + string.Join("; ", stale));

        foreach (var action in actions)
        {
            var policies = action.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(d => d.Policy).ToList();
            var anonymous = action.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var actual = anonymous ? Access.Anonymous
                : policies.Contains(Policies.AdminOnly) ? Access.AdminOnly
                : policies.Contains(Policies.ManagerOrAdmin) ? Access.ManagerOrAdmin
                : Access.SignedIn;
            Assert.True(Matrix[action.Key] == actual, $"{action.Key}: metadata says {actual}, matrix says {Matrix[action.Key]}");

            // Every state-changing action keeps the global antiforgery check (only the error pages opt out: they change nothing).
            if (action.Method != "GET")
            {
                var ignores = action.Endpoint.Metadata.GetMetadata<IgnoreAntiforgeryTokenAttribute>() is not null;
                Assert.True(!ignores || action.Key.StartsWith("ANY /error", StringComparison.Ordinal), $"{action.Key} skips antiforgery");
            }
        }
    }

    private static string Url(string template) =>
        Regex.Replace(template.Replace("/{controller=Home}/{action=Index}/{id?}", "/", StringComparison.Ordinal), "\\{[^}]+\\}", m => m.Value.Contains(":int", StringComparison.Ordinal) ? "999999" : "missing-id");

    private static bool IsLoginRedirect(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.OriginalString.Contains("/account/login", StringComparison.OrdinalIgnoreCase) == true;

    [Fact]
    public async Task Every_routed_action_gives_the_expected_outcome_for_anonymous_Manager_and_Admin()
    {
        var anonymous = App.CreateHttpsClient();
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var managerToken = await manager.GetAntiforgeryTokenAsync("/");
        var adminToken = await admin.GetAntiforgeryTokenAsync("/");
        var problems = new List<string>();

        // Sign-out ends the session: it is checked at the end with its own clients.
        foreach (var (key, access) in Matrix.Where(m => m.Key != "POST /account/logout"))
        {
            var method = key[..key.IndexOf(' ', StringComparison.Ordinal)];
            var url = key.StartsWith("ANY /error", StringComparison.Ordinal) ? "/error/404" : Url(key[(key.IndexOf(' ', StringComparison.Ordinal) + 1)..]);

            async Task<HttpResponseMessage> Send(HttpClient client, string? token) => method == "POST"
                ? await client.PostAsync(url, new FormUrlEncodedContent(token is null ? [] : new Dictionary<string, string> { ["__RequestVerificationToken"] = token }))
                : await client.GetAsync(url);

            var anon = await Send(anonymous, null);
            if (access == Access.Anonymous ? IsLoginRedirect(anon) && !url.StartsWith("/account", StringComparison.Ordinal) : !IsLoginRedirect(anon))
            {
                problems.Add($"anonymous {key}: {(int)anon.StatusCode} {anon.Headers.Location}");
            }

            var asManager = await Send(manager, managerToken);
            var managerOk = access == Access.AdminOnly ? asManager.StatusCode == HttpStatusCode.Forbidden : asManager.StatusCode != HttpStatusCode.Forbidden && !IsLoginRedirect(asManager);
            if (!managerOk)
            {
                problems.Add($"Manager {key}: {(int)asManager.StatusCode}");
            }

            var asAdmin = await Send(admin, adminToken);
            if (asAdmin.StatusCode == HttpStatusCode.Forbidden || IsLoginRedirect(asAdmin) || asAdmin.StatusCode == HttpStatusCode.InternalServerError)
            {
                problems.Add($"Admin {key}: {(int)asAdmin.StatusCode}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        // Sign-out: anonymous → login; signed in → signed out.
        Assert.True(IsLoginRedirect(await anonymous.PostAsync("/account/logout", new FormUrlEncodedContent([]))));
        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync("/account/logout", "/")).StatusCode);
    }

    [Fact]
    public async Task Every_POST_without_an_antiforgery_token_is_rejected_with_400()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var anonymous = App.CreateHttpsClient();
        var problems = new List<string>();
        foreach (var key in Matrix.Keys.Where(k => k.StartsWith("POST ", StringComparison.Ordinal)))
        {
            var url = Url(key[5..]);
            var client = Matrix[key] == Access.Anonymous ? anonymous : admin;
            var response = await client.PostAsync(url, new FormUrlEncodedContent([]));
            if (response.StatusCode != HttpStatusCode.BadRequest)
            {
                problems.Add($"{key}: {(int)response.StatusCode}");
            }
        }

        Assert.True(problems.Count == 0, "POSTs accepted without a token: " + string.Join("; ", problems));
    }

    // ===================== Object-level checks =====================

    private async Task AddRateAsync(DateOnly from, decimal rate)
    {
        await using var scope = App.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ExchangeRateService>().CreateAsync(new RateInput(from, rate, "<script>alert(1)</script>"), true, "test")).Succeeded);
    }

    private async Task<int> PayAsync(int personId, AdminPayInput input, string? note = null)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PayRecordService>().CreateAdminAsync(personId, input, note, confirmLoss: true, "test");
        Assert.True(result.Succeeded);
        return result.Id!.Value;
    }

    private static Dictionary<string, string> Period(DateOnly start) => new()
    {
        ["Month"] = start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        ["Half"] = start.Day >= 16 ? "16" : "1",
    };

    private static async Task<int> GenerateAsync(HttpClient client, DateOnly start)
    {
        var response = await client.PostFormAsync("/payroll/generate", "/payroll", Period(start));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(Regex.Match(response.Headers.Location!.OriginalString, @"/payroll/(\d+)$").Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<PayrollLine>> LinesAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollLines.AsNoTracking().Include(l => l.Adjustments).Where(l => l.RunId == runId).ToListAsync();
    }

    [Fact]
    public async Task Ids_that_belong_to_another_parent_are_404_and_Managers_cannot_edit_each_others_records()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var a = await App.CreatePersonAsync("Parent A", joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        var b = await App.CreatePersonAsync("Parent B", joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        await PayAsync(a, new AdminPayInput(Oct(1), 300m, 25m, null, null, null));
        var recordB = await PayAsync(b, new AdminPayInput(Oct(1), 300m, 25m, null, null, null));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run1 = await GenerateAsync(admin, Oct(1));
        var run2 = await GenerateAsync(admin, Oct(16));
        var line1 = (await LinesAsync(run1)).First();
        await admin.PostFormAsync($"/payroll/{run1}/lines/{line1.Id}/adjustments", $"/payroll/{run1}/lines/{line1.Id}",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "10", ["Currency"] = "USD", ["Note"] = "x" });
        var adjustment = (await LinesAsync(run1)).Single(l => l.Id == line1.Id).Adjustments.Single();
        var line2 = (await LinesAsync(run2)).First(l => l.PersonId != line1.PersonId);

        // A line of run 1 addressed under run 2, and an adjustment of line 1 under line 2.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/payroll/{run2}/lines/{line1.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/payroll/{run2}/lines/{line1.Id}/payslip/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/payroll/{run2}/lines/{line2.Id}/adjustments/{adjustment.Id}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostFormAsync($"/payroll/{run2}/lines/{line2.Id}/adjustments/{adjustment.Id}/delete", "/")).StatusCode);
        // Person B's rate record under person A.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/people/{a}/pay/{recordB}/edit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostFormAsync($"/people/{a}/pay/{recordB}/delete", "/")).StatusCode);

        // Manager 1 records an increment for A; Manager 2 may neither edit nor delete it.
        var (manager1, _) = await App.SignInAsAsync(AppRoles.Manager);
        var increment = await manager1.PostFormAsync($"/people/{a}/pay/increment", $"/people/{a}/pay/increment",
            new Dictionary<string, string> { ["EffectiveMonth"] = "2026-11", ["EffectiveHalf"] = "1", ["Pay"] = "321" });
        Assert.Equal(HttpStatusCode.Redirect, increment.StatusCode);
        int incrementId;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            incrementId = await db.RateRecords.Where(r => r.PersonId == a).OrderByDescending(r => r.Id).Select(r => r.Id).FirstAsync();
        }

        var (manager2, _) = await App.SignInAsAsync(AppRoles.Manager);
        // Refused as "not found": another Manager's record is not even confirmed to exist.
        Assert.Equal(HttpStatusCode.NotFound, (await manager2.PostFormAsync($"/people/{a}/pay/{incrementId}/increment-edit", "/",
            new Dictionary<string, string> { ["EffectiveMonth"] = "2026-11", ["EffectiveHalf"] = "1", ["Pay"] = "999", ["RowVersion"] = "AAAAAAAAAAA=" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager2.PostFormAsync($"/people/{a}/pay/{incrementId}/delete", "/")).StatusCode);
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Equal(321m, await db.RateRecords.Where(r => r.Id == incrementId).Select(r => r.PayMonthlyAmount).SingleAsync());
        }
    }

    // ===================== Stored XSS =====================

    private static readonly string[] Payloads =
    [
        "<script>alert(1)</script>",
        "\"><img src=x onerror=alert(1)>",
        "javascript:alert(1)",
        "{{7*7}}",
        "</text><svg onload=alert(1)>",
        "' onmouseover='alert(1)",
    ];

    private static void AssertNoLiveMarkup(string where, string html)
    {
        Assert.DoesNotMatch(new Regex("<script(?![^>]*\\ssrc=\"/)", RegexOptions.IgnoreCase), html.Replace("<script src=", "<script src=", StringComparison.Ordinal));
        // Event-handler attributes: look at attribute names only (quoted values are inert text, even "x onclick=y").
        foreach (Match tag in Regex.Matches(html, "<[a-zA-Z][^>]*>"))
        {
            var names = Regex.Replace(tag.Value, "\"[^\"]*\"|'[^']*'", "\"\"");
            Assert.False(Regex.IsMatch(names, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase), $"{where}: event-handler attribute in {tag.Value}");
        }
        Assert.DoesNotMatch(new Regex("(href|src|action|formaction|xlink:href)\\s*=\\s*[\"']?\\s*javascript:", RegexOptions.IgnoreCase), html);
        foreach (var raw in new[] { "<script>alert(1)</script>", "<img src=x onerror", "<svg onload", "onmouseover='alert" })
        {
            Assert.False(html.Contains(raw, StringComparison.OrdinalIgnoreCase), $"{where} contains the raw payload {raw}");
        }
    }

    private async Task<(int Run, int Line, int Adjustment, int Absence, int Rate, int Record, int Person, int Invoice)> SeedPayloadsAsync(HttpClient admin)
    {
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var current = await settings.GetAsync();
            Assert.True((await settings.UpdateAsync(new SettingsInput(Payloads[0], Payloads[1], "billing@tower.example", null, Payloads[4], Payloads[5], null, null,
                Payloads[4], Payloads[2], Payloads[1], null, "XSS", 7, Payloads[2], Payloads[3]), current.RowVersion, "test")).Succeeded);
        }

        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        int rate;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            rate = await db.ExchangeRates.Select(r => r.Id).SingleAsync();
        }

        var person = await App.CreatePersonAsync(Payloads[0], designation: Payloads[1], joined: new DateOnly(2025, 1, 6), source: HireSource.CompanyRecommended);
        var other = await App.CreatePersonAsync(Payloads[5], designation: Payloads[4], joined: new DateOnly(2025, 1, 6), source: HireSource.BudgetHire);
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            await db.People.Where(p => p.Id == person).ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, Payloads[2]).SetProperty(p => p.BankName, Payloads[3]));
            db.Absences.Add(Absence.Create(person, Oct(5), AbsencePortion.Full, Payloads[0], "test", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var record = await PayAsync(person, new AdminPayInput(Oct(1), 300m, 25m, null, null, null), Payloads[1]);
        await PayAsync(other, new AdminPayInput(Oct(1), null, null, 1000m, 196_000m, PayCurrency.PKR), Payloads[4]);
        int absence;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            absence = await db.Absences.Select(a => a.Id).SingleAsync();
        }

        var run = await GenerateAsync(admin, Oct(1));
        var line = (await LinesAsync(run)).Single(l => l.PersonId == person);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/payroll/{run}/lines/{line.Id}/adjustments", $"/payroll/{run}/lines/{line.Id}",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "10", ["Currency"] = "USD", ["Note"] = Payloads[0] })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/payroll/{run}/lines/{line.Id}/extra-days", $"/payroll/{run}/lines/{line.Id}",
            new Dictionary<string, string> { ["ExtraDays"] = "1", ["ExtraDaysNote"] = Payloads[1] })).StatusCode);
        var adjustment = (await LinesAsync(run)).Single(l => l.Id == line.Id).Adjustments.Single().Id;
        await admin.PostFormAsync($"/payroll/{run}/finalize", $"/payroll/{run}");
        int invoice;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            invoice = await db.Invoices.Where(i => i.RunId == run).Select(i => i.Id).SingleAsync();
        }

        await App.CreateUserAsync(AppRoles.Manager, fullName: Payloads[0]);
        return (run, line.Id, adjustment, absence, rate, record, person, invoice);
    }

    private static async Task<(long Dbts, string Counts)> StateAsync()
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        var dbts = await db.Database.SqlQueryRaw<long>("SELECT CONVERT(bigint, @@DBTS) AS [Value]").SingleAsync();
        var counts = string.Join(",", await db.People.CountAsync(), await db.RateRecords.CountAsync(), await db.Absences.CountAsync(), await db.ExchangeRates.CountAsync(),
            await db.PayrollRuns.CountAsync(), await db.PayrollLines.CountAsync(), await db.PayrollAdjustments.CountAsync(), await db.Invoices.CountAsync(), await db.Users.CountAsync());
        return (dbts, counts);
    }

    [Fact]
    public async Task Payloads_only_ever_appear_encoded_on_every_page_and_export_and_no_GET_changes_state()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedPayloadsAsync(admin);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        string managerId;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            managerId = (await db.Users.FirstAsync(u => u.FullName == Payloads[0])).Id;
        }

        var pages = new List<string>
        {
            "/", "/people?status=All", $"/people/{s.Person}", $"/people/{s.Person}/edit", $"/people/{s.Person}?tab=absences&month=2026-10", $"/people/{s.Person}?tab=payslips",
            "/absences?period=2026-10-01&status=All", $"/absences/{s.Absence}/edit", "/absences/day?date=2026-10-05", "/salaries",
            "/exchange-rates", $"/exchange-rates/{s.Rate}/edit", "/payroll", $"/payroll/{s.Run}", $"/payroll/{s.Run}/lines/{s.Line}",
            $"/payroll/{s.Run}/lines/{s.Line}/payslip", $"/payroll/{s.Run}/payslips", $"/payroll/{s.Run}/register",
            "/reports", "/reports/payroll-history", "/reports/salary-changes?from=2025-11-01&to=2026-10-31", "/reports/absences?from=2026-01-01&to=2026-12-31", "/reports/headcount",
            $"/people/{s.Person}/pay/increment",
        };
        var adminPages = new List<string>
        {
            $"/people/{s.Person}/pay/{s.Record}/edit", $"/people/{s.Person}/pay/new", "/invoices", $"/invoices/{s.Invoice}", "/owner-income?view=Month&at=2026-10-01",
            "/admin/settings", "/admin/managers", $"/admin/managers/{managerId}/edit", "/admin/audit", "/account/security",
        };

        var before = await StateAsync();
        var seenEncoded = false;
        foreach (var (client, list) in new[] { (admin, pages.Concat(adminPages).ToList()), (manager, pages) })
        {
            foreach (var url in list)
            {
                var response = await client.GetAsync(url);
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}");
                var html = await response.Content.ReadAsStringAsync();
                AssertNoLiveMarkup(url, html);
                seenEncoded |= html.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal);
            }
        }

        Assert.True(seenEncoded, "The payloads were not rendered at all; the test would prove nothing.");

        // Exports: payloads are plain text cells (never formulas) and literal text in PDFs.
        foreach (var url in new[] { "/people/export?status=All", "/absences/export?period=2026-10-01&status=All", "/exchange-rates/export", $"/payroll/{s.Run}/export",
                     $"/payroll/{s.Run}/register/export", $"/invoices/{s.Invoice}/export", "/admin/audit/export" })
        {
            var bytes = await admin.GetByteArrayAsync(url);
            using var workbook = new XLWorkbook(new MemoryStream(bytes));
            var cells = workbook.Worksheets.SelectMany(w => w.CellsUsed()).ToList();
            Assert.DoesNotContain(cells, c => c.HasFormula);
            Assert.All(cells.Where(c => Payloads.Any(p => c.GetString() == p)), c => Assert.Equal(XLDataType.Text, c.DataType));
        }

        foreach (var url in new[] { $"/invoices/{s.Invoice}/pdf", $"/payroll/{s.Run}/payslips/pdf", $"/payroll/{s.Run}/register/pdf" })
        {
            var bytes = await admin.GetByteArrayAsync(url);
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4), StringComparison.Ordinal);
            using var pdf = PdfDocument.Open(bytes);
            Assert.NotEmpty(pdf.GetPages().First().Text);
        }

        // Reading never writes: the database rowversion counter and every business table's row count are unchanged.
        Assert.Equal(before, await StateAsync());
    }

    // ===================== Cookies and headers =====================

    private static void AssertSecurityHeaders(string where, HttpResponseMessage response)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.Single()
            : response.Content.Headers.TryGetValues(name, out var content) ? content.Single() : null;
        Assert.True(Header("Content-Security-Policy") == SecurityHeadersMiddleware.ContentSecurityPolicy, $"{where}: CSP");
        Assert.True(Header("X-Content-Type-Options") == "nosniff", $"{where}: nosniff");
        Assert.True(Header("X-Frame-Options") == "DENY", $"{where}: X-Frame-Options");
        Assert.True(Header("Referrer-Policy") == "strict-origin-when-cross-origin", $"{where}: Referrer-Policy");
        Assert.True(Header("Permissions-Policy") == SecurityHeadersMiddleware.PermissionsPolicy, $"{where}: Permissions-Policy");
        Assert.True(Header("Cross-Origin-Opener-Policy") == "same-origin", $"{where}: COOP");
        Assert.True(Header("Cross-Origin-Resource-Policy") == "same-origin", $"{where}: CORP");
        Assert.True(Header("Server") is null && Header("X-Powered-By") is null, $"{where}: Server/X-Powered-By");
    }

    private static void AssertHostCookie(string setCookie)
    {
        Assert.StartsWith("__Host-", setCookie, StringComparison.Ordinal);
        var attributes = setCookie.ToLowerInvariant();
        Assert.Contains("; secure", attributes);
        Assert.Contains("; path=/", attributes);
        Assert.Contains("; httponly", attributes);
        Assert.Contains("samesite=lax", attributes);
        Assert.DoesNotContain("domain=", attributes);
    }

    [Fact]
    public async Task Cookies_are_Host_prefixed_and_every_response_carries_the_security_headers()
    {
        var anonymous = App.CreateHttpsClient();
        var login = await anonymous.GetAsync("/account/login");
        AssertSecurityHeaders("login page", login);
        AssertHostCookie(login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieSecurity.AntiforgeryCookieName + "=", StringComparison.Ordinal)));

        var user = await App.CreateUserAsync(AppRoles.Manager);
        var signedIn = await anonymous.PostLoginAsync(user.Email, user.Password);
        AssertHostCookie(signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthCookie.Name + "=", StringComparison.Ordinal)));

        var toast = await anonymous.PostFormAsync("/people/create", "/people/create", PeopleHelpers.Form("Cookie Person"));
        AssertHostCookie(toast.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieSecurity.TempDataCookieName + "=", StringComparison.Ordinal)));

        AssertSecurityHeaders("page", await anonymous.GetAsync("/people"));
        var css = await anonymous.GetAsync("/css/site.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        AssertSecurityHeaders("static file", css);
        var missing = await anonymous.GetAsync("/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        AssertSecurityHeaders("error page", missing);
        var download = await anonymous.GetAsync("/people/export");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        AssertSecurityHeaders("download", download);
        AssertSecurityHeaders("health", await App.CreateHttpsClient().GetAsync("/health"));

        // The two-factor step's cookie is __Host- too.
        var admin = await App.CreateUserAsync(AppRoles.Admin);
        var twoFactorClient = App.CreateHttpsClient();
        var token = await twoFactorClient.GetAntiforgeryTokenAsync("/account/login");
        var step = await twoFactorClient.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = admin.Email, ["Password"] = admin.Password, ["__RequestVerificationToken"] = token,
        }));
        Assert.StartsWith(AccountPaths.TwoFactor, step.Headers.Location!.OriginalString, StringComparison.Ordinal);
        AssertHostCookie(step.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthCookie.TwoFactorName + "=", StringComparison.Ordinal)));
    }

    private static class AccountPaths
    {
        public const string TwoFactor = "/account/login-2fa";
        public const string Setup = "/account/two-factor/setup";
    }

    // ===================== Two-factor =====================

    [Fact]
    public async Task An_Admin_without_two_factor_must_enrol_before_anything_else()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin, enrolTwoFactor: false);
        var client = App.CreateHttpsClient();
        var login = await client.PostLoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        foreach (var url in new[] { "/", "/people", "/admin/audit", "/invoices" })
        {
            var gated = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, gated.StatusCode);
            Assert.Equal(AccountPaths.Setup, gated.Headers.Location!.OriginalString);
        }

        var setup = await client.GetStringAsync(AccountPaths.Setup);
        Assert.Contains("data-testid=\"two-factor-required\"", setup);
        Assert.Matches("src=\"data:image/png;base64,[A-Za-z0-9+/=&#;]{100,}\"", setup); // Razor encodes + and / as entities
        var key = Regex.Match(setup, "data-testid=\"manual-key\">([a-z0-9 ]+)<").Groups[1].Value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        Assert.Equal(32, key.Length);
        Totp.UseClock(key, App.Time);

        var wrong = await client.PostFormAsync(AccountPaths.Setup, AccountPaths.Setup, new Dictionary<string, string> { ["Code"] = Totp.WrongCode(key) });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("didn&#x27;t match", await wrong.Content.ReadAsStringAsync());

        var enrolled = await client.PostFormAsync(AccountPaths.Setup, AccountPaths.Setup, new Dictionary<string, string> { ["Code"] = await Totp.NextCodeAsync(key) });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        Assert.Contains("no-store", enrolled.Headers.CacheControl?.ToString() ?? string.Empty);
        var codes = Regex.Matches(await enrolled.Content.ReadAsStringAsync(), "data-testid=\"recovery-code\">([^<]+)<").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.True((await App.GetUserAsync(user.Id)).TwoFactorEnabled);
        // The secret is never shown again once enrolled.
        Assert.Equal("/account/security", (await client.GetAsync(AccountPaths.Setup)).Headers.Location!.OriginalString);
        await AssertAuditedAsync(AuditEvents.TwoFactorEnabled, user.Id);
    }

    [Fact]
    public async Task A_valid_code_signs_in_and_wrong_codes_count_towards_lockout()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin);
        var good = App.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Redirect, (await good.PostLoginAsync(user.Email, user.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await good.GetAsync("/")).StatusCode);

        // A fresh session: password right, then five wrong codes → locked; even the right code then fails.
        AuthHelpers.ForgetAuthenticator(user.Email);
        var client = App.CreateHttpsClient();
        var step = await client.PostLoginAsync(user.Email, user.Password);
        Assert.StartsWith(AccountPaths.TwoFactor, step.Headers.Location!.OriginalString, StringComparison.Ordinal);
        for (var i = 0; i < 5; i++)
        {
            var wrong = await client.PostTwoFactorCodeAsync(Totp.WrongCode(user.AuthenticatorKey!));
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("That code didn", await wrong.Content.ReadAsStringAsync());
        }

        Assert.True((await App.GetUserAsync(user.Id)).LockoutEnd > DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.PostTwoFactorCodeAsync(await Totp.NextCodeAsync(user.AuthenticatorKey!))).StatusCode); // refused, page again
        Assert.True(IsLoginRedirect(await client.GetAsync("/")));
        await AssertAuditedAsync(AuditEvents.TwoFactorFailed, user.Id);
        await AssertAuditedAsync(AuditEvents.LockedOut, user.Id);

        // "Remember this device" is never offered or set.
        Assert.DoesNotContain("RememberMachine", await App.CreateHttpsClient().GetStringAsync(AccountPaths.TwoFactor));
    }

    [Fact]
    public async Task A_recovery_code_signs_in_exactly_once()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin);
        string code;
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            code = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(user.Id))!, 10))!.First();
        }

        AuthHelpers.ForgetAuthenticator(user.Email);
        async Task<HttpResponseMessage> RecoverAsync()
        {
            var client = App.CreateHttpsClient();
            await client.PostLoginAsync(user.Email, user.Password);
            var token = await client.GetAntiforgeryTokenAsync("/account/login-recovery");
            var response = await client.PostAsync("/account/login-recovery", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["RecoveryCode"] = code, ["__RequestVerificationToken"] = token,
            }));
            return response;
        }

        var first = await RecoverAsync();
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/", first.Headers.Location!.OriginalString);
        var second = await RecoverAsync();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("That code didn", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, (await App.GetUserAsync(user.Id)).AccessFailedCount);
        await AssertAuditedAsync(AuditEvents.RecoveryCodeUsed, user.Id);
    }

    [Fact]
    public async Task The_Admin_can_require_two_factor_for_a_Manager_and_reset_it()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var manager = await App.CreateUserAsync(AppRoles.Manager);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/admin/managers/{manager.Id}/two-factor/require", $"/admin/managers/{manager.Id}/edit",
            new Dictionary<string, string> { ["required"] = "true" })).StatusCode);

        var client = await App.CreateSignedInClientAsync(manager);
        var gated = await client.GetAsync("/people");
        Assert.Equal(AccountPaths.Setup, gated.Headers.Location!.OriginalString);

        // Enrolled, then the Admin resets it: the Manager's session ends and the code step is gone.
        var key = await App.EnrolTwoFactorAsync(manager.Id);
        var enrolled = await App.CreateSignedInClientAsync(manager with { AuthenticatorKey = key });
        Assert.Equal(HttpStatusCode.OK, (await enrolled.GetAsync("/people")).StatusCode);
        Assert.Contains("data-testid=\"manager-2fa-status\">On", await admin.GetStringAsync($"/admin/managers/{manager.Id}/edit"));
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync($"/admin/managers/{manager.Id}/two-factor/reset", $"/admin/managers/{manager.Id}/edit")).StatusCode);
        AuthHelpers.ForgetAuthenticator(manager.Email);
        App.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(IsLoginRedirect(await enrolled.GetAsync("/people")));
        var stored = await App.GetUserAsync(manager.Id);
        Assert.False(stored.TwoFactorEnabled);
        var again = App.CreateHttpsClient();
        Assert.Equal("/", (await again.PostLoginAsync(manager.Email, manager.Password)).Headers.Location!.OriginalString);
        Assert.Equal(AccountPaths.Setup, (await again.GetAsync("/people")).Headers.Location!.OriginalString); // still required: enrol again
        await AssertAuditedAsync(AuditEvents.ManagerTwoFactorRequirementChanged, manager.Id);
        await AssertAuditedAsync(AuditEvents.ManagerTwoFactorReset, manager.Id);

        // Managers may not touch each other's two-factor.
        var (otherManager, _) = await App.SignInAsAsync(AppRoles.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherManager.PostFormAsync($"/admin/managers/{manager.Id}/two-factor/reset", "/")).StatusCode);
    }

    // ===================== Sessions =====================

    [Fact]
    public async Task Signing_out_ends_every_other_session_of_the_same_user()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        var laptop = await App.CreateSignedInClientAsync(user);
        var phone = await App.CreateSignedInClientAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/people")).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await laptop.PostFormAsync("/account/logout", "/")).StatusCode);
        App.Time.Advance(TimeSpan.FromMinutes(2)); // past the security-stamp validation interval
        Assert.True(IsLoginRedirect(await phone.GetAsync("/people")));
        await AssertAuditedAsync(AuditEvents.LoggedOut, user.Id);
    }

    [Fact]
    public async Task Signing_in_issues_a_new_session_cookie()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        var client = App.CreateDefaultClient(new Uri("https://localhost"));
        client.DefaultRequestHeaders.Add(HrWebApplicationFactory.ClientIpHeader, HrWebApplicationFactory.NextClientIp());
        var page = await client.GetAsync("/account/login");
        var antiforgery = page.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CookieSecurity.AntiforgeryCookieName, StringComparison.Ordinal)).Split(';')[0];
        var token = WebUtility.HtmlDecode(Regex.Match(await page.Content.ReadAsStringAsync(), "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value);

        // A planted session cookie (fixation attempt) is replaced, never adopted.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/account/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = user.Email, ["Password"] = user.Password, ["__RequestVerificationToken"] = token }),
        };
        request.Headers.Add("Cookie", $"{antiforgery}; {AuthCookie.Name}=planted-by-attacker");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var issued = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthCookie.Name + "=", StringComparison.Ordinal));
        Assert.DoesNotContain("planted-by-attacker", issued);
        Assert.True(issued.Length > 200);
    }

    // ===================== Emergency Admin recovery =====================

    [Fact]
    public async Task The_admin_reset_console_command_recovers_an_Admin_and_is_audited()
    {
        var user = await App.CreateUserAsync(AppRoles.Admin);
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = (await users.FindByIdAsync(user.Id))!;
            await users.SetLockoutEndDateAsync(stored, DateTimeOffset.UtcNow.AddHours(1));
        }

        var output = new StringWriter();
        int exit;
        await using (var scope = App.Services.CreateAsyncScope())
        {
            exit = await scope.ServiceProvider.GetRequiredService<ServerCommands>().RunAsync([ServerCommands.AdminReset, "--email", user.Email], output);
        }

        Assert.Equal(0, exit);
        var password = Regex.Match(output.ToString(), "Temporary password \\(shown once, not stored anywhere\\): (\\S+)").Groups[1].Value;
        Assert.True(password.Length >= 12);
        var reset = await App.GetUserAsync(user.Id);
        Assert.False(reset.TwoFactorEnabled);
        Assert.True(reset.MustChangePassword);
        Assert.Null(reset.LockoutEnd);

        AuthHelpers.ForgetAuthenticator(user.Email);
        var client = App.CreateHttpsClient();
        Assert.Equal(ForcePasswordChangeMiddleware.ChangePasswordPath, (await client.PostLoginAsync(user.Email, password)).Headers.Location!.OriginalString);
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1040);
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains(password, StringComparison.Ordinal));
        await AssertAuditedAsync(AuditEvents.AdminResetCommand, user.Id);

        // Not an Admin, or unknown: refused. And there is no HTTP route for it.
        var manager = await App.CreateUserAsync(AppRoles.Manager);
        await using (var scope = App.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ServerCommands>().RunAsync([ServerCommands.AdminReset, "--email", manager.Email], new StringWriter()));
        }

        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin-reset")).StatusCode);
    }

    // ===================== Audit trail =====================

    private static async Task AssertAuditedAsync(AuditEvent auditEvent, string? entityId = null)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.True(await db.AuditLog.AnyAsync(a => a.EventId == auditEvent.Id && (entityId == null || a.EntityId == entityId)),
            $"No audit row for {auditEvent.Name} ({entityId})");
    }

    [Fact]
    public async Task The_audit_log_records_events_across_modules_without_sensitive_values_and_is_append_only()
    {
        const string cnic = "35202-7654321-9";
        const string iban = "PK36SCBL0000001123456702";
        const string phone = "03451239876";
        const string note = "Private medical appointment";
        const string reason = "Owner asked to add a bonus for Ayesha";
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var person = await App.CreatePersonAsync("Audit Person", phone: phone, cnic: cnic, iban: iban, joined: new DateOnly(2025, 1, 6), source: HireSource.Owner);
        await PayAsync(person, new AdminPayInput(Oct(1), 1200m, null, null, null, null), note);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync("/absences/new", "/absences/new", new Dictionary<string, string>
        {
            ["PersonId"] = person.ToString(CultureInfo.InvariantCulture), ["Date"] = "2026-10-05", ["Portion"] = "Full", ["Note"] = note,
        })).StatusCode);
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var current = await settings.GetAsync();
            Assert.True((await settings.UpdateAsync(new SettingsInput("Tower Staffing", null, null, null, "Meezan", "Tower", iban, null, "Client Corp", null, null, null, "AUD", 7, null, "Tower"),
                current.RowVersion, adminUser.Id)).Succeeded);
        }

        var run = await GenerateAsync(admin, Oct(1));
        await admin.PostFormAsync($"/payroll/{run}/finalize", $"/payroll/{run}");
        await admin.PostFormAsync($"/payroll/{run}/reopen", $"/payroll/{run}", new Dictionary<string, string> { ["Reason"] = reason });
        await admin.GetAsync("/people/export");

        await using var db = TestDatabaseFixture.CreateDbContext();
        var rows = await db.AuditLog.AsNoTracking().ToListAsync();
        foreach (var expected in new[] { AuditEvents.LoginSucceeded, AuditEvents.PersonCreated, AuditEvents.RateRecordCreated, AuditEvents.AbsenceCreated, AuditEvents.ExchangeRateCreated,
                     AuditEvents.SettingsChanged, AuditEvents.PayrollGenerated, AuditEvents.PayrollFinalized, AuditEvents.InvoiceIssued, AuditEvents.PayrollReopened, AuditEvents.InvoiceVoided, AuditEvents.Exported })
        {
            Assert.True(rows.Any(r => r.EventId == expected.Id), $"No {expected.Name} row");
        }

        var finalized = rows.Single(r => r.EventId == AuditEvents.PayrollFinalized.Id);
        Assert.Equal(adminUser.Id, finalized.ActorUserId);
        Assert.Equal("Test Admin", finalized.ActorName);
        Assert.False(string.IsNullOrEmpty(finalized.ActorIp));
        Assert.Equal(run.ToString(CultureInfo.InvariantCulture), finalized.EntityId);
        Assert.Contains("BankAccountNumber", rows.Single(r => r.EventId == AuditEvents.SettingsChanged.Id).Summary); // the field name, not the value

        var everything = string.Join("\n", rows.Select(r => $"{r.ActorName}|{r.ActorIp}|{r.EntityId}|{r.Summary}"));
        foreach (var secret in new[] { cnic, iban, "6702", phone, "1239876", note, reason, AuthHelpers.DefaultPassword })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }

        // Append-only, whoever asks.
        foreach (var sql in new[] { "UPDATE [AuditLog] SET [Summary] = N'changed'", "DELETE FROM [AuditLog]" })
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(51030, ex.Number);
        }

        Assert.Equal(rows.Count, await db.AuditLog.CountAsync());
    }

    [Fact]
    public async Task The_audit_page_filters_pages_and_exports_and_is_Admin_only()
    {
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        for (var i = 0; i < 3; i++)
        {
            await App.CreatePersonAsync($"Audit Filter {i}");
        }

        var all = await admin.GetStringAsync("/admin/audit");
        Assert.Contains("data-testid=\"retention\">keep forever", all);
        var people = await admin.GetStringAsync($"/admin/audit?eventId={AuditEvents.PersonCreated.Id}");
        Assert.Equal(3, Regex.Matches(people, "data-testid=\"audit-row\"").Count);
        Assert.DoesNotContain("LoginSucceeded</td>", people);
        var mine = await admin.GetStringAsync($"/admin/audit?user={adminUser.Id}");
        Assert.Matches("</span> LoginSucceeded</td>", mine);
        Assert.DoesNotContain("</span> PersonCreated</td>", mine); // created by "test-setup", not this Admin
        Assert.Contains("data-testid=\"audit-empty\"", await admin.GetStringAsync("/admin/audit?from=2001-01-01&to=2001-01-02"));
        Assert.Equal(3, Regex.Matches(await admin.GetStringAsync("/admin/audit?entity=Person"), "data-testid=\"audit-row\"").Count);

        var export = await admin.GetAsync($"/admin/audit/export?eventId={AuditEvents.PersonCreated.Id}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using (var workbook = new XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync())))
        {
            Assert.Equal(4, workbook.Worksheet("Audit log").RangeUsed()!.RowCount()); // header + 3
        }

        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/admin/audit/export")).StatusCode);
        Assert.DoesNotContain("href=\"/admin/audit\"", await manager.GetStringAsync("/"));
    }

    [Fact]
    public async Task The_dashboard_alerts_the_Admin_to_lockouts_failed_sign_ins_and_reopens()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        Assert.DoesNotContain("data-testid=\"security-alert\"", await admin.GetStringAsync("/"));
        var victim = await App.CreateUserAsync(AppRoles.Manager);
        var attacker = App.CreateHttpsClient();
        for (var i = 0; i < 5; i++)
        {
            await attacker.PostLoginAsync(victim.Email, "Wrong-Password-1");
        }

        var html = await admin.GetStringAsync("/");
        Assert.Contains("data-testid=\"security-alert\"", html);
        Assert.Contains("1 account lockout", html);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        Assert.DoesNotContain("security-alert", await manager.GetStringAsync("/"));
    }

    // ===================== Rate limits =====================

    [Fact]
    public async Task Exports_and_changes_are_rate_limited_per_user()
    {
        var (manager, managerUser) = await App.SignInAsAsync(AppRoles.Manager);
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/exchange-rates/export")).StatusCode);
        }

        var limited = await manager.GetAsync("/exchange-rates/export");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/people")).StatusCode); // pages are not limited

        var token = await manager.GetAntiforgeryTokenAsync("/people/create");
        var form = new Dictionary<string, string>(PeopleHelpers.Form(fullName: string.Empty)) { ["__RequestVerificationToken"] = token };
        for (var i = 0; i < 120; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await manager.PostAsync("/people/create", new FormUrlEncodedContent(form))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await manager.PostAsync("/people/create", new FormUrlEncodedContent(form))).StatusCode);
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(2, await db.AuditLog.CountAsync(a => a.EventId == AuditEvents.RequestRateLimited.Id && a.ActorUserId == managerUser.Id)); // once per limit, not per request

        // Another user is unaffected.
        var (other, _) = await App.SignInAsAsync(AppRoles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/exchange-rates/export")).StatusCode);
    }

    // ===================== Health, failures, outages =====================

    [Fact]
    public async Task Health_answers_anonymously_with_no_details()
    {
        var response = await App.CreateHttpsClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private sealed class FailInvoiceInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            eventData.Context!.ChangeTracker.Entries<HR.Domain.Invoices.Invoice>().Any(e => e.State == EntityState.Added)
                ? throw new InvalidOperationException("Simulated failure in the middle of finalize")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_finalize_leaves_nothing_behind()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var person = await App.CreatePersonAsync("Rollback Person", joined: new DateOnly(2025, 1, 6), source: HireSource.Owner);
        await PayAsync(person, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var current = await settings.GetAsync();
            await settings.UpdateAsync(new SettingsInput("Tower", null, null, null, null, null, null, null, "Client", null, null, null, "RB", 7, null, "Tower"), current.RowVersion, "test");
        }

        var failing = App.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(new FailInvoiceInsert()))));
        var user = await App.CreateUserAsync(AppRoles.Admin);
        var client = failing.CreateHttpsClient();
        await client.PostLoginAsync(user.Email, user.Password);
        var run = await GenerateAsync(client, Oct(1));
        var response = await client.PostFormAsync($"/payroll/{run}/finalize", $"/payroll/{run}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await using var db = TestDatabaseFixture.CreateDbContext();
        var stored = await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == run);
        Assert.Equal(PayrollStatus.Draft, stored.Status);
        Assert.Null(stored.FinalizedAt);
        Assert.False(await db.Invoices.AnyAsync());
        Assert.False(await db.Set<HR.Domain.Invoices.InvoiceCounter>().AnyAsync());
        Assert.False(await db.AuditLog.AnyAsync(a => a.EventId == AuditEvents.PayrollFinalized.Id || a.EventId == AuditEvents.InvoiceIssued.Id));
        Assert.All(await db.PayrollLines.AsNoTracking().Where(l => l.RunId == run).ToListAsync(), l => Assert.Equal(PayrollStatus.Draft, stored.Status));
    }

    [Fact]
    public async Task Double_submits_of_finalize_issue_and_mark_paid_change_things_only_once()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var person = await App.CreatePersonAsync("Double Person", joined: new DateOnly(2025, 1, 6), source: HireSource.Owner);
        await PayAsync(person, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        var token = await admin.GetAntiforgeryTokenAsync($"/payroll/{run}");
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token });
        var finalizes = await Task.WhenAll(admin.PostAsync($"/payroll/{run}/finalize", form), admin.PostAsync($"/payroll/{run}/finalize", form));
        Assert.All(finalizes, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Equal(1, await db.AuditLog.CountAsync(a => a.EventId == AuditEvents.PayrollFinalized.Id));
        }

        // Settings were empty, so the invoice is pending; two "Issue invoice" clicks issue one.
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            var current = await settings.GetAsync();
            await settings.UpdateAsync(new SettingsInput("Tower", null, null, null, null, null, null, null, "Client", null, null, null, "DB", 7, null, "Tower"), current.RowVersion, "test");
        }

        var issues = await Task.WhenAll(admin.PostAsync($"/invoices/issue/{run}", form), admin.PostAsync($"/invoices/issue/{run}", form));
        Assert.All(issues, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));
        int invoiceId;
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            invoiceId = (await db.Invoices.SingleAsync()).Id;
        }

        var page = await admin.GetStringAsync($"/invoices/{invoiceId}");
        var rowVersion = WebUtility.HtmlDecode(Regex.Match(page, "name=\"RowVersion\" value=\"([^\"]*)\"").Groups[1].Value);
        var paid = new Dictionary<string, string>
        {
            ["PaidDate"] = PeopleHelpers.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["AmountReceivedUsd"] = "600.00", ["RowVersion"] = rowVersion,
            ["__RequestVerificationToken"] = await admin.GetAntiforgeryTokenAsync($"/invoices/{invoiceId}"),
        };
        await Task.WhenAll(admin.PostAsync($"/invoices/{invoiceId}/paid", new FormUrlEncodedContent(paid)), admin.PostAsync($"/invoices/{invoiceId}/paid", new FormUrlEncodedContent(paid)));
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Equal(1, await db.AuditLog.CountAsync(a => a.EventId == AuditEvents.InvoicePaid.Id));
            Assert.Equal(1, await db.AuditLog.CountAsync(a => a.EventId == AuditEvents.InvoiceIssued.Id));
        }
    }

    [Fact]
    public async Task An_unreachable_database_gives_a_friendly_503_with_a_correlation_id()
    {
        var down = Fixture.Production.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Server=tcp:127.0.0.1,1;Database=HRPayroll_Test;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=2",
        })));
        var client = down.CreateHttpsClient();
        var login = await client.GetAsync("/account/login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); // pages that need no database still work

        var response = await client.PostFormAsync("/account/login", "/account/login", new Dictionary<string, string> { ["Email"] = "someone@example.test", ["Password"] = "Whatever-123" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Temporarily unavailable", html);
        Assert.Matches("data-testid=\"correlation-id\">[0-9a-f]{16,}<", html);
        foreach (var leak in new[] { "SqlException", "Exception", "stack", "127.0.0.1", "at HR." })
        {
            Assert.DoesNotContain(leak, html, StringComparison.Ordinal);
        }

        var health = await down.CreateHttpsClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        Assert.Equal("Unhealthy", await health.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Production_ignores_development_features_even_when_configured()
    {
        var production = Fixture.Production.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DemoData:Seed"] = "true",
            ["Database:MigrateOnStartup"] = "true",
        })));
        var user = await App.CreateUserAsync(AppRoles.Admin);
        var client = production.CreateHttpsClient();
        await client.PostLoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/styleguide")).StatusCode);
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(0, await db.People.CountAsync()); // no demo data
        Assert.Contains(Fixture.Production.Logs.Entries.Concat(App.Logs.Entries), e => e.Message.Contains("MigrateOnStartup is ignored outside Development", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Data_protection_keys_persist_so_a_restarted_app_accepts_existing_cookies()
    {
        var user = await App.CreateUserAsync(AppRoles.Manager);
        var cookies = new System.Net.CookieContainer();
        var first = App.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(cookies));
        first.DefaultRequestHeaders.Add(HrWebApplicationFactory.ClientIpHeader, HrWebApplicationFactory.NextClientIp());
        Assert.Equal(HttpStatusCode.Redirect, (await first.PostLoginAsync(user.Email, user.Password)).StatusCode);

        // A brand-new host (an app restart) with the same key folder.
        await using var restarted = new HrWebApplicationFactory("Development");
        var second = restarted.CreateDefaultClient(new Uri("https://localhost"), new CookieContainerHandler(cookies));
        var page = await second.GetAsync("/people");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains(user.Email.Split('@')[0].Split('.')[0], (await page.Content.ReadAsStringAsync()).ToLowerInvariant());

        // The key ring is on disk and DPAPI-encrypted (no plaintext master key).
        var keyFiles = Directory.GetFiles(HrWebApplicationFactory.KeysPath, "key-*.xml");
        Assert.NotEmpty(keyFiles);
        Assert.All(keyFiles, f =>
        {
            var xml = File.ReadAllText(f);
            Assert.Contains("encryptedSecret", xml);
            Assert.DoesNotContain("<masterKey", xml);
        });
    }
}
