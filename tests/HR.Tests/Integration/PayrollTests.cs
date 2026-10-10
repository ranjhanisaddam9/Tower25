using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.Payroll;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Security;
using HR.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HR.Tests.Integration;

public partial class PayrollTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static readonly DateOnly LongAgo = new(2025, 1, 6);

    // ---------- Setup helpers (through the real services) ----------

    private sealed record GoldenPeople(int Ayesha, int Nadia, int Kamran, int Bilal, int Imran, int Rizwan);

    private async Task AddRateAsync(DateOnly from, decimal rate)
    {
        await using var scope = App.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ExchangeRateService>().CreateAsync(new RateInput(from, rate, null), true, "test")).Succeeded);
    }

    private async Task PayAsync(int personId, AdminPayInput input)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<PayRecordService>().CreateAdminAsync(personId, input, "setup", confirmLoss: true, "test-admin");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors?.Select(e => e.Message) ?? [result.Status.ToString()]));
    }

    private static AdminPayInput Cr(decimal salary = 300m, decimal commission = 25m) => new(Oct(1), salary, commission, null, null, null);

    private static AdminPayInput Budget(decimal budget = 1000m, decimal pay = 196_000m) => new(Oct(1), null, null, budget, pay, PayCurrency.PKR);

    private static AdminPayInput OwnerPay(decimal salary = 1200m) => new(Oct(1), salary, null, null, null, null);

    private static async Task AbsentAsync(int personId, DateOnly date, AbsencePortion portion = AbsencePortion.Full)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        db.Absences.Add(Absence.Create(personId, date, portion, null, "test", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>The SPEC §9 people G1–G7 with their pay and absences, and rate 280.</summary>
    private async Task<GoldenPeople> SeedGoldenAsync()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var ayesha = await App.CreatePersonAsync("Ayesha Golden", joined: LongAgo, source: HireSource.CompanyRecommended);
        var nadia = await App.CreatePersonAsync("Nadia Golden", joined: Oct(8), source: HireSource.CompanyRecommended);
        var kamran = await App.CreatePersonAsync("Kamran Golden", joined: LongAgo, source: HireSource.CompanyRecommended);
        var bilal = await App.CreatePersonAsync("Bilal Golden", joined: LongAgo, source: HireSource.BudgetHire);
        var imran = await App.CreatePersonAsync("Imran Golden", joined: LongAgo, source: HireSource.Owner);
        var rizwan = await App.CreatePersonAsync("Rizwan Golden", joined: LongAgo, source: HireSource.CompanyRecommended, left: Oct(21));

        foreach (var id in new[] { ayesha, nadia, kamran, rizwan })
        {
            await PayAsync(id, Cr());
        }

        await PayAsync(bilal, Budget());
        await PayAsync(imran, OwnerPay());

        await AbsentAsync(kamran, Oct(5), AbsencePortion.Half);
        await AbsentAsync(kamran, Oct(7));
        await AbsentAsync(bilal, Oct(6));
        await AbsentAsync(bilal, Oct(20));
        await AbsentAsync(bilal, Oct(27));
        return new GoldenPeople(ayesha, nadia, kamran, bilal, imran, rizwan);
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
        var match = Regex.Match(response.Headers.Location!.OriginalString, @"/payroll/(\d+)$");
        Assert.True(match.Success, $"Unexpected redirect {response.Headers.Location}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> FinalizeAsync(HttpClient client, int runId) =>
        await client.PostFormAsync($"/payroll/{runId}/finalize", $"/payroll/{runId}");

    private static async Task<List<PayrollLine>> LinesAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollLines.AsNoTracking().Include(l => l.Adjustments).Include(l => l.Absences)
            .Where(l => l.RunId == runId).OrderBy(l => l.PersonName).ToListAsync();
    }

    private static async Task<PayrollRun> RunAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private static string? Toast(string html) => ToastRegex().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;

    private static string Encoded(string text) => WebUtility.HtmlEncode(text).Replace("&#39;", "&#x27;", StringComparison.Ordinal);

    private static void AssertLine(PayrollLine l, decimal payable, decimal billed, decimal payUsd, decimal payPkr, decimal ownerUsd, decimal ownerPkr)
    {
        Assert.Equal(11, l.WorkingDays);
        Assert.Equal(payable, l.PayableDays);
        Assert.Equal(billed, l.BilledUsd);
        Assert.Equal(payUsd, l.PayUsd);
        Assert.Equal(payPkr, l.PayPkr);
        Assert.Equal(ownerUsd, l.OwnerEarningUsd);
        Assert.Equal(ownerPkr, l.OwnerEarningPkr);
        Assert.Null(l.Issue);
    }

    // ===================== End-to-end golden =====================

    [Fact]
    public async Task Golden_G1_to_G8_end_to_end_then_finalize_and_the_snapshot_never_changes()
    {
        var g = await SeedGoldenAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var first = await GenerateAsync(admin, Oct(1));
        var second = await GenerateAsync(admin, Oct(16));
        Assert.Equal(280m, (await RunAsync(first)).ExchangeRate);

        // G8: Bilal's Oct 16–31 line + Reimbursement Rs 5,000, Bonus $20, Deduction Rs 1,000.
        var bilalSecond = (await LinesAsync(second)).Single(l => l.PersonId == g.Bilal).Id;
        foreach (var (type, amount, currency) in new[] { ("Reimbursement", "5000", "PKR"), ("Bonus", "20", "USD"), ("Deduction", "1000", "PKR") })
        {
            var add = await admin.PostFormAsync($"/payroll/{second}/lines/{bilalSecond}/adjustments", $"/payroll/{second}/lines/{bilalSecond}",
                new Dictionary<string, string> { ["Type"] = type, ["Amount"] = amount, ["Currency"] = currency, ["Note"] = "golden" });
            Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        }

        var lines1 = (await LinesAsync(first)).ToDictionary(l => l.PersonId);
        var lines2 = (await LinesAsync(second)).ToDictionary(l => l.PersonId);
        Assert.Equal(6, lines1.Count);
        Assert.Equal(6, lines2.Count);

        AssertLine(lines1[g.Ayesha], 11m, 175.00m, 150.00m, 42_000m, 25.00m, 7_000m); // G1
        AssertLine(lines1[g.Nadia], 6m, 95.46m, 81.82m, 22_910m, 13.64m, 3_819m); // G2
        AssertLine(lines1[g.Kamran], 10.5m, 167.04m, 143.18m, 40_090m, 23.86m, 6_681m); // G3
        AssertLine(lines1[g.Bilal], 11m, 500.00m, 350.00m, 98_000m, 150.00m, 42_000m); // G4
        AssertLine(lines2[g.Imran], 11m, 600.00m, 600.00m, 168_000m, 0.00m, 0m); // G6
        AssertLine(lines2[g.Rizwan], 4m, 63.64m, 54.55m, 15_274m, 9.09m, 2_545m); // G7

        // G5 base values on Bilal's Oct 16–31 line, then the G8 finals with the adjustments.
        var g8 = lines2[g.Bilal];
        Assert.Equal((9m, 409.09m, 286.36m, 80_182m), (g8.PayableDays, g8.BilledUsd!.Value, g8.PayUsd!.Value, g8.PayPkr!.Value));
        Assert.Equal((443.38m, 89_782m, 320.65m, 122.73m, 34_364m), (g8.InvoiceUsd!.Value, g8.NetPayPkr!.Value, g8.NetPayUsd!.Value, g8.OwnerEarningUsd!.Value, g8.OwnerEarningPkr!.Value));
        Assert.Equal([(5_000m, 17.86m), (5_600m, 20.00m), (1_000m, 3.57m)], g8.Adjustments.OrderBy(a => a.Id).Select(a => (a.AmountPkr!.Value, a.AmountUsd!.Value)).ToArray());
        Assert.Equal([(Oct(20), 0m, 1m), (Oct(27), 0m, 1m)], g8.Absences.OrderBy(a => a.Date).Select(a => (a.Date, a.PaidDays, a.UnpaidDays)).ToArray());

        // Owner income Oct 16–31 (G6 + full-period CompanyRecommended lines + G8/G5 + G7), as SPEC §8.
        var ownerIncome = lines2.Values.Sum(l => l.HireSource == HireSource.Owner ? l.NetPayUsd!.Value : l.OwnerEarningUsd!.Value);
        Assert.Equal(600.00m + 25.00m * 3 + 122.73m + 9.09m, ownerIncome);

        // Finalize both, in order.
        Assert.Equal("Payroll finalized. Its figures are now locked.", Toast(await admin.FollowAsync(await FinalizeAsync(admin, first))));
        Assert.Equal("Payroll finalized. Its figures are now locked.", Toast(await admin.FollowAsync(await FinalizeAsync(admin, second))));
        Assert.All(new[] { await RunAsync(first), await RunAsync(second) }, r => Assert.Equal(PayrollStatus.Finalized, r.Status));

        // Afterwards: rename the person, change the exchange rate, try to change the pay record.
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            await db.People.Where(p => p.Id == g.Bilal).ExecuteUpdateAsync(s => s.SetProperty(p => p.FullName, "Renamed Person").SetProperty(p => p.Designation, "Changed"));
            await db.ExchangeRates.ExecuteUpdateAsync(s => s.SetProperty(r => r.UsdToPkr, 300m));
        }

        await AddRateAsync(Oct(16), 310m);
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var pay = scope.ServiceProvider.GetRequiredService<PayRecordService>();
            var record = await scope.ServiceProvider.GetRequiredService<HR.Infrastructure.Data.AppDbContext>().RateRecords.AsNoTracking().SingleAsync(r => r.PersonId == g.Bilal);
            var edit = await pay.UpdateAdminAsync(g.Bilal, record.Id, new AdminPayInput(Oct(1), null, null, 1200m, 250_000m, PayCurrency.PKR), null, false, true, record.RowVersion, "test");
            Assert.Equal(PayResultStatus.Locked, edit.Status);
        }

        var after = (await LinesAsync(second)).Single(l => l.PersonId == g.Bilal);
        Assert.Equal(("Bilal Golden", 443.38m, 89_782m, 34_364m, 196_000m), (after.PersonName, after.InvoiceUsd!.Value, after.NetPayPkr!.Value, after.OwnerEarningPkr!.Value, after.PayMonthlyAmount!.Value));
        Assert.Equal(280m, (await RunAsync(second)).ExchangeRate);
        Assert.Contains("Bilal Golden", await admin.GetStringAsync($"/payroll/{second}"));
    }

    [Fact]
    public async Task Golden_G9_and_G10_through_extra_days()
    {
        var g = await SeedGoldenAsync();
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var first = await GenerateAsync(manager, Oct(1));
        var second = await GenerateAsync(manager, Oct(16));
        var ayesha = (await LinesAsync(first)).Single(l => l.PersonId == g.Ayesha).Id;
        var bilal = (await LinesAsync(second)).Single(l => l.PersonId == g.Bilal).Id;

        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync($"/payroll/{first}/lines/{ayesha}/extra-days", $"/payroll/{first}/lines/{ayesha}",
            new Dictionary<string, string> { ["ExtraDays"] = "1", ["ExtraDaysNote"] = "Sat 3 Oct" })).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync($"/payroll/{second}/lines/{bilal}/extra-days", $"/payroll/{second}/lines/{bilal}",
            new Dictionary<string, string> { ["ExtraDays"] = "1.5", ["ExtraDaysNote"] = "Sat 17 Oct, Sun 18 Oct half" })).StatusCode);

        var g9 = (await LinesAsync(first)).Single(l => l.Id == ayesha);
        AssertLine(g9, 12m, 190.91m, 163.64m, 45_819m, 27.27m, 7_636m);
        Assert.Equal((1m, "Sat 3 Oct"), (g9.ExtraDays, g9.ExtraDaysNote));

        var g10 = (await LinesAsync(second)).Single(l => l.Id == bilal);
        AssertLine(g10, 10.5m, 477.27m, 334.09m, 93_545m, 143.18m, 40_091m);

        // The Manager sees the days with the extra part, and a plain-language pay breakdown.
        Assert.Contains("10.5 of 11 incl. 1.5 extra", await manager.GetStringAsync($"/payroll/{second}"));
        var line = WebUtility.HtmlDecode(await manager.GetStringAsync($"/payroll/{second}/lines/{bilal}"));
        Assert.Contains("Rs 196,000 ÷ 2 × 10.5/11 = Rs 93,545", line);

        // Extra-days limits over HTTP.
        foreach (var bad in new[] { "0.25", "10.5", "-1" })
        {
            var response = await manager.PostFormAsync($"/payroll/{first}/lines/{ayesha}/extra-days", $"/payroll/{first}/lines/{ayesha}",
                new Dictionary<string, string> { ["ExtraDays"] = bad, ["ExtraDaysNote"] = "x" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(ExtraDaysRules.RangeMessage, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(1m, (await LinesAsync(first)).Single(l => l.Id == ayesha).ExtraDays);
        var cleared = await manager.PostFormAsync($"/payroll/{first}/lines/{ayesha}/extra-days", $"/payroll/{first}/lines/{ayesha}", new Dictionary<string, string> { ["clear"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, cleared.StatusCode);
        AssertLine((await LinesAsync(first)).Single(l => l.Id == ayesha), 11m, 175.00m, 150.00m, 42_000m, 25.00m, 7_000m);
    }

    // ===================== Workflow =====================

    [Fact]
    public async Task Generation_defaults_to_the_period_after_the_latest_finalized_and_refuses_earlier_periods()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Default Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, new AdminPayInput(new DateOnly(2026, 1, 1), 1200m, null, null, null, null));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var today = PayPeriod.For(PeopleHelpers.Today);
        Assert.Contains($"value=\"{today.Start:yyyy-MM}\"", await admin.GetStringAsync("/payroll"));
        Assert.Contains("data-testid=\"payroll-tile\"", await admin.GetStringAsync("/"));
        Assert.Matches("data-testid=\"payroll-tile\"[\\s\\S]*?stat-value\">Not started<", await admin.GetStringAsync("/"));

        var sep = await GenerateAsync(admin, new DateOnly(2026, 9, 1));
        Assert.Equal(HttpStatusCode.Redirect, (await FinalizeAsync(admin, sep)).StatusCode);
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(sep)).Status);

        // Default is now Sep 16–30; an earlier period is refused; an existing one opens the existing run.
        Assert.Contains("value=\"2026-09\"", await admin.GetStringAsync("/payroll"));
        Assert.Contains("id=\"Half-16\" value=\"16\" checked=\"checked\"", await admin.GetStringAsync("/payroll"));
        var earlier = await admin.PostFormAsync("/payroll/generate", "/payroll", Period(new DateOnly(2026, 8, 16)));
        Assert.Contains(Encoded(PayrollService.BeforeFinalizedMessage), await admin.FollowAsync(earlier));
        var again = await admin.PostFormAsync("/payroll/generate", "/payroll", Period(new DateOnly(2026, 9, 1)));
        Assert.Equal($"/payroll/{sep}", again.Headers.Location?.OriginalString);

        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(1, await db.PayrollRuns.CountAsync());
    }

    [Fact]
    public async Task Lines_cover_everyone_employed_in_the_period_and_a_missing_rate_is_flagged()
    {
        var a = await App.CreatePersonAsync("Employed Alpha", joined: LongAgo, source: HireSource.Owner);
        await App.CreatePersonAsync("Left Before", joined: LongAgo, left: new DateOnly(2026, 9, 30), source: HireSource.BudgetHire);
        await App.CreatePersonAsync("Weekend Joiner", joined: new DateOnly(2026, 10, 3)); // Sat: Oct 1–2 are before, Oct 5+ inside
        var later = await App.CreatePersonAsync("Joins Later", joined: new DateOnly(2026, 10, 16));
        await PayAsync(a, OwnerPay());
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var run = await GenerateAsync(manager, Oct(1));
        var lines = await LinesAsync(run);
        Assert.Equal(["Employed Alpha", "Weekend Joiner"], lines.Select(l => l.PersonName).ToArray());
        Assert.DoesNotContain(lines, l => l.PersonId == later);
        Assert.Equal(LineIssue.NoHireSource, lines.Single(l => l.PersonName == "Weekend Joiner").Issue);

        var html = await manager.GetStringAsync($"/payroll/{run}");
        Assert.Contains("data-testid=\"no-rate\"", html);
        Assert.Contains("Set exchange rate", html);
        Assert.Contains("Pay setup incomplete", html);
        Assert.Null((await RunAsync(run)).ExchangeRate);
        Assert.Null(lines.Single(l => l.PersonId == a).PayPkr); // USD pay needs the rate for PKR
        Assert.Equal(600.00m, lines.Single(l => l.PersonId == a).PayUsd);
    }

    [Fact]
    public async Task Finalize_is_refused_for_issues_missing_rate_earlier_drafts_and_changed_data()
    {
        var ok = await App.CreatePersonAsync("Ready Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(ok, OwnerPay());
        var noSource = await App.CreatePersonAsync("Unsourced Person", joined: LongAgo);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        // 1. No rate, and an issue.
        var first = await GenerateAsync(manager, Oct(1));
        Assert.Contains(Encoded(PayrollService.NoRateMessage), await manager.FollowAsync(await FinalizeAsync(manager, first)));

        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var setRate = await manager.PostFormAsync($"/payroll/{first}/rate", $"/payroll/{first}", new Dictionary<string, string> { ["Source"] = "history" });
        Assert.Equal(HttpStatusCode.Redirect, setRate.StatusCode);
        Assert.Equal(280m, (await RunAsync(first)).ExchangeRate);
        Assert.Contains(Encoded(PayrollService.IssuesMessage), await manager.FollowAsync(await FinalizeAsync(manager, first)));

        // Fix the issue (delete the person), regenerate.
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            await db.PayrollLines.Where(l => l.PersonId == noSource).ExecuteDeleteAsync();
            await db.People.Where(p => p.Id == noSource).ExecuteDeleteAsync();
        }

        // 2. An earlier draft blocks a later finalize (out of order).
        var second = await GenerateAsync(manager, Oct(16));
        await manager.PostFormAsync($"/payroll/{second}/rate", $"/payroll/{second}", new Dictionary<string, string> { ["Source"] = "history" });
        Assert.Contains(Encoded(PayrollService.EarlierDraftMessage), await manager.FollowAsync(await FinalizeAsync(manager, second)));

        // 3. Data changed since the draft: an absence added behind the draft's back.
        await AbsentAsync(ok, Oct(7));
        await AbsentAsync(ok, Oct(8));
        var changed = await FinalizeAsync(manager, first);
        var page = WebUtility.HtmlDecode(await manager.FollowAsync(changed));
        Assert.Contains(PayrollService.DataChangedMessage, page);
        Assert.Contains("data-testid=\"changes\"", page);
        Assert.Contains("Ready Person", page);
        Assert.Contains("Unpaid days 0.00 → 1.00", page);
        Assert.Contains("Payable days 11.00 → 10.00", page);
        Assert.DoesNotContain("Billed", page);
        Assert.Equal(PayrollStatus.Draft, (await RunAsync(first)).Status);
        Assert.Equal(1m, (await LinesAsync(first)).Single().UnpaidDays); // the draft was recalculated

        // Now it matches: finalize, then the later one.
        Assert.Equal(HttpStatusCode.Redirect, (await FinalizeAsync(manager, first)).StatusCode);
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(first)).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await FinalizeAsync(manager, second)).StatusCode);
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(second)).Status);
    }

    [Fact]
    public async Task Regenerate_keeps_extra_days_and_adjustments_and_lists_orphans()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var stays = await App.CreatePersonAsync("Stays Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(stays, OwnerPay());
        var goes = await App.CreatePersonAsync("Goes Person", joined: LongAgo, source: HireSource.CompanyRecommended);
        await PayAsync(goes, Cr());
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var run = await GenerateAsync(admin, Oct(1));
        var lines = (await LinesAsync(run)).ToDictionary(l => l.PersonId);
        await admin.PostFormAsync($"/payroll/{run}/lines/{lines[stays].Id}/extra-days", $"/payroll/{run}/lines/{lines[stays].Id}",
            new Dictionary<string, string> { ["ExtraDays"] = "2", ["ExtraDaysNote"] = "Sat 3, Sun 4 Oct" });
        await admin.PostFormAsync($"/payroll/{run}/lines/{lines[stays].Id}/adjustments", $"/payroll/{run}/lines/{lines[stays].Id}",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "10000", ["Currency"] = "PKR" });
        await admin.PostFormAsync($"/payroll/{run}/lines/{lines[goes].Id}/adjustments", $"/payroll/{run}/lines/{lines[goes].Id}",
            new Dictionary<string, string> { ["Type"] = "Reimbursement", ["Amount"] = "2500", ["Currency"] = "PKR" });

        // Data changes: an absence for one, the other's employment moves out of the period.
        await AbsentAsync(stays, Oct(6));
        await AbsentAsync(stays, Oct(7));
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            await db.EmploymentPeriods.Where(e => e.PersonId == goes).ExecuteUpdateAsync(s => s.SetProperty(e => e.StartDate, Oct(19)));
            await db.People.Where(p => p.Id == goes).ExecuteUpdateAsync(s => s.SetProperty(p => p.JoiningDate, Oct(19)));
        }

        var regenerate = await admin.PostFormAsync($"/payroll/{run}/regenerate", $"/payroll/{run}");
        var page = WebUtility.HtmlDecode(await admin.FollowAsync(regenerate));
        Assert.Contains("data-testid=\"orphans\"", page);
        Assert.Contains("Goes Person", page);

        var after = (await LinesAsync(run)).ToDictionary(l => l.PersonId);
        var kept = after[stays];
        Assert.Equal((2m, "Sat 3, Sun 4 Oct", 1, 1m), (kept.ExtraDays, kept.ExtraDaysNote, kept.Adjustments.Count, kept.UnpaidDays));
        Assert.Equal(12m, kept.PayableDays); // 11 − 1 unpaid + 2 extra
        Assert.Equal(Money.RoundPkr(Money.RoundUsd(600m * 12m / 11m) * 280m) + 10_000m, kept.NetPayPkr);
        Assert.True(after[goes].IsOrphaned);
        Assert.Single(after[goes].Adjustments);

        Assert.Contains(Encoded(PayrollService.OrphansMessage), await admin.FollowAsync(await FinalizeAsync(admin, run)));

        // Deleting the orphan's last entry removes its line.
        var orphanAdjustment = after[goes].Adjustments.Single().Id;
        await admin.PostFormAsync($"/payroll/{run}/lines/{after[goes].Id}/adjustments/{orphanAdjustment}/delete", $"/payroll/{run}");
        Assert.DoesNotContain(await LinesAsync(run), l => l.PersonId == goes);
        Assert.Equal(HttpStatusCode.Redirect, (await FinalizeAsync(admin, run)).StatusCode);
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(run)).Status);
    }

    [Fact]
    public async Task A_large_recalculation_diff_does_not_break_the_session()
    {
        // Regression: the diff once travelled in the TempData cookie and, with many people, made later requests fail.
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var ids = new List<int>();
        for (var i = 0; i < 25; i++)
        {
            var id = await App.CreatePersonAsync($"Bulk Person {i:00}", joined: LongAgo, source: HireSource.CompanyRecommended);
            await PayAsync(id, Cr());
            ids.Add(id);
        }

        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var run = await GenerateAsync(manager, Oct(1));
        foreach (var id in ids)
        {
            await AbsentAsync(id, Oct(6));
            await AbsentAsync(id, Oct(7));
        }

        var regenerate = await manager.PostFormAsync($"/payroll/{run}/regenerate", $"/payroll/{run}");
        var page = WebUtility.HtmlDecode(await manager.FollowAsync(regenerate));
        Assert.Contains("Recalculated:", page);
        Assert.Contains("Bulk Person 24", page);
        Assert.Contains("Unpaid days 0.00 → 1.00", page);
        Assert.True(regenerate.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.All(c => c.Length < 4000));

        // The session still works afterwards, and the diff is shown only once.
        var again = await manager.GetAsync($"/payroll/{run}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.DoesNotContain("data-testid=\"changes\"", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Changing_the_rate_recomputes_PKR_and_needs_a_note()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Rate Person", joined: LongAgo, source: HireSource.CompanyRecommended);
        await PayAsync(id, Cr());
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var run = await GenerateAsync(manager, Oct(1));
        Assert.Equal(42_000m, (await LinesAsync(run)).Single().PayPkr);

        var noNote = await manager.PostFormAsync($"/payroll/{run}/rate", $"/payroll/{run}", new Dictionary<string, string> { ["Source"] = "override", ["Rate"] = "300" });
        Assert.Contains(Encoded(PayrollService.RateNoteMessage), await manager.FollowAsync(noNote));
        Assert.Equal(280m, (await RunAsync(run)).ExchangeRate);

        await manager.PostFormAsync($"/payroll/{run}/rate", $"/payroll/{run}", new Dictionary<string, string> { ["Source"] = "override", ["Rate"] = "300", ["RateNote"] = "Bank's rate on payday" });
        var stored = await RunAsync(run);
        Assert.Equal((300m, true, "Bank's rate on payday", (int?)null), (stored.ExchangeRate!.Value, stored.RateOverridden, stored.RateNote, stored.ExchangeRateEntryId));
        var line = (await LinesAsync(run)).Single();
        Assert.Equal((150.00m, 45_000m, 175.00m, 7_500m), (line.PayUsd!.Value, line.PayPkr!.Value, line.BilledUsd!.Value, line.OwnerEarningPkr!.Value));
        Assert.Contains("Overridden", await manager.GetStringAsync($"/payroll/{run}"));
    }

    [Fact]
    public async Task Reopen_is_Admin_only_needs_a_reason_and_only_the_latest()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Reopen Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, OwnerPay());
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var first = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, first);
        var second = await GenerateAsync(admin, Oct(16));
        await FinalizeAsync(admin, second);

        var reason = new Dictionary<string, string> { ["Reason"] = "Wrong bonus amount for Ali" };
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostFormAsync($"/payroll/{second}/reopen", $"/payroll/{second}", reason)).StatusCode);
        Assert.DoesNotContain("data-testid=\"reopen\"", await manager.GetStringAsync($"/payroll/{second}"));

        var notLatest = await admin.PostFormAsync($"/payroll/{first}/reopen", $"/payroll/{first}", reason);
        Assert.Contains(Encoded(PayrollService.ReopenLatestOnlyMessage), await admin.FollowAsync(notLatest));

        var shortReason = await admin.PostFormAsync($"/payroll/{second}/reopen", $"/payroll/{second}", new Dictionary<string, string> { ["Reason"] = "too short" });
        Assert.Contains(Encoded(PayrollService.ReopenReasonMessage), await admin.FollowAsync(shortReason));
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(second)).Status);

        var ok = await admin.PostFormAsync($"/payroll/{second}/reopen", $"/payroll/{second}", reason);
        var page = await admin.FollowAsync(ok);
        Assert.Equal(PayrollStatus.Draft, (await RunAsync(second)).Status);
        Assert.Contains("Wrong bonus amount for Ali", page); // in the run's history, not in the logs
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("Wrong bonus", StringComparison.Ordinal));
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1509 && e.Values.Contains($"RunId={second}"));

        // Finalize again.
        Assert.Equal(HttpStatusCode.Redirect, (await FinalizeAsync(admin, second)).StatusCode);
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(second)).Status);
    }

    [Fact]
    public async Task Drafts_can_be_deleted_finalized_runs_never_not_even_directly_in_the_database()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Delete Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, OwnerPay());
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var draft = await GenerateAsync(manager, Oct(16));
        Assert.Equal(HttpStatusCode.Redirect, (await manager.PostFormAsync($"/payroll/{draft}/delete", $"/payroll/{draft}")).StatusCode);
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.False(await db.PayrollRuns.AnyAsync(r => r.Id == draft));
            Assert.False(await db.PayrollLines.AnyAsync(l => l.RunId == draft));
        }

        var final = await GenerateAsync(manager, Oct(1));
        await FinalizeAsync(manager, final);
        var refused = await manager.PostFormAsync($"/payroll/{final}/delete", $"/payroll/{final}");
        Assert.Contains("A finalized payroll can never be deleted.", await manager.FollowAsync(refused));

        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [PayrollRuns] WHERE [Id] = {final}"));
            Assert.Equal(PayrollService.NoDeleteFinalizedErrorNumber, delete.Number);
            var edit = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [PayrollLines] SET [NetPayPkr] = 1 WHERE [RunId] = {final}"));
            Assert.Equal(PayrollService.FrozenErrorNumber, edit.Number);
            Assert.True(await db.PayrollRuns.AnyAsync(r => r.Id == final));
        }

        // Drafts of a finalized run can't get extra days or adjustments either.
        var line = (await LinesAsync(final)).Single().Id;
        var extra = await manager.PostFormAsync($"/payroll/{final}/lines/{line}/extra-days", $"/payroll/{final}/lines/{line}", new Dictionary<string, string> { ["ExtraDays"] = "1", ["ExtraDaysNote"] = "x" });
        Assert.Contains(Encoded(PayrollService.NotDraftMessage), await manager.FollowAsync(extra));
    }

    [Fact]
    public async Task A_finalized_period_locks_absences_pay_records_and_employment_but_not_exchange_rates()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Locked Person", joined: LongAgo, source: HireSource.CompanyRecommended);
        var leaver = await App.CreatePersonAsync("Early Leaver", joined: LongAgo, left: new DateOnly(2026, 9, 25));
        await PayAsync(id, Cr());
        await AbsentAsync(id, Oct(5));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, run);
        Assert.Equal(PayrollStatus.Finalized, (await RunAsync(run)).Status);

        await using var scope = App.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var absences = sp.GetRequiredService<AbsenceService>();
        var pay = sp.GetRequiredService<PayRecordService>();
        var people = sp.GetRequiredService<PersonService>();
        var db = sp.GetRequiredService<HR.Infrastructure.Data.AppDbContext>();

        // Absences: create, edit, delete in Oct 1–15 refused; Oct 16+ fine.
        Assert.Equal(AbsenceResultStatus.Locked, (await absences.CreateAsync(id, Oct(6), AbsencePortion.Full, null, "t")).Status);
        var existing = await db.Absences.AsNoTracking().SingleAsync(a => a.PersonId == id);
        Assert.Equal(AbsenceResultStatus.Locked, (await absences.UpdateAsync(existing.Id, AbsencePortion.Half, null, existing.RowVersion, "t")).Status);
        Assert.Equal(AbsenceResultStatus.Locked, (await absences.DeleteAsync(existing.Id, "t")).Status);
        Assert.True((await absences.CreateAsync(id, Oct(19), AbsencePortion.Full, null, "t")).Succeeded);

        // Pay records: create, edit, delete in the locked period refused.
        Assert.Equal(PayResultStatus.Locked, (await pay.CreateAdminAsync(id, new AdminPayInput(Oct(1), 400m, 25m, null, null, null), null, true, "t")).Status);
        var record = await db.RateRecords.AsNoTracking().SingleAsync(r => r.PersonId == id);
        Assert.Equal(PayResultStatus.Locked, (await pay.UpdateAdminAsync(id, record.Id, new AdminPayInput(Oct(1), 400m, 25m, null, null, null), null, false, true, record.RowVersion, "t")).Status);
        Assert.Equal(PayResultStatus.Locked, (await pay.DeleteAsync(id, record.Id, "t", true)).Status);
        Assert.True((await pay.CreateAdminAsync(id, new AdminPayInput(Oct(16), 400m, 25m, null, null, null), null, true, "t")).Succeeded);

        // Employment: a leaving date inside (or before) the locked period, or a joining date moved across it, is refused.
        var inside = await people.DeactivateAsync(id, Oct(10), "t");
        Assert.Contains("finalized payroll for 01 Oct–15 Oct 2026", inside.Errors![0].Message);
        var before = await people.DeactivateAsync(id, new DateOnly(2026, 9, 30), "t");
        Assert.Equal(PersonResultStatus.Invalid, before.Status);
        Assert.True((await people.DeactivateAsync(id, Oct(15), "t")).Succeeded); // the last day of the locked period changes nothing in it
        // Rejoining inside the locked period adds employment to it: refused; after it, fine.
        Assert.Contains("finalized payroll", (await people.ReactivateAsync(leaver, Oct(5), "t", revealHireSource: true)).Errors![0].Message);
        Assert.True((await people.ReactivateAsync(leaver, Oct(19), "t", revealHireSource: true)).Succeeded);

        var joiner = await App.CreatePersonAsync("Late Joiner", joined: Oct(19));
        var details = await people.GetAsync(joiner);
        var move = await people.UpdateAsync(joiner, new PersonInput(details!.FullName, details.Type, details.Designation, null, details.Phone, null, null, null, Oct(5), null), true, true, details.RowVersion, "t");
        Assert.Contains("finalized payroll", move.Errors![0].Message);

        // Exchange rates can still change, and the finalized run keeps its own rate.
        await db.ExchangeRates.ExecuteUpdateAsync(x => x.SetProperty(r => r.UsdToPkr, 300m));
        Assert.Equal(280m, (await RunAsync(run)).ExchangeRate);
        Assert.Equal(42_000m, (await LinesAsync(run)).Single().PayPkr);
    }

    // ===================== Visibility =====================

    private static readonly string[] ManagerForbidden =
    [
        "Billed", "Invoice", "earning", "Commission", "Margin", "Owner", "Company recommended", "CompanyRecommended", "Budget hire", "BudgetHire",
        "443.38", "409.09", "175.00", "122.73", "34,364",
    ];

    [Fact]
    public async Task Managers_never_see_billing_on_any_payroll_page_and_payslips_have_none_for_anyone()
    {
        var g = await SeedGoldenAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var first = await GenerateAsync(admin, Oct(1));
        var second = await GenerateAsync(admin, Oct(16));
        var bilal = (await LinesAsync(second)).Single(l => l.PersonId == g.Bilal).Id;
        await admin.PostFormAsync($"/payroll/{second}/lines/{bilal}/adjustments", $"/payroll/{second}/lines/{bilal}",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "20", ["Currency"] = "USD" });
        await FinalizeAsync(admin, first);

        var ayeshaFirst = (await LinesAsync(first)).Single(l => l.PersonId == g.Ayesha).Id;
        var urls = new[]
        {
            "/", "/payroll", $"/payroll/{first}", $"/payroll/{second}", $"/payroll/{second}/lines/{bilal}", $"/payroll/{first}/lines/{ayeshaFirst}",
            $"/payroll/{second}/lines/{bilal}/payslip", $"/payroll/{second}/payslips", $"/payroll/{first}/register", $"/payroll/{second}/register",
        };
        foreach (var url in urls)
        {
            var response = await manager.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
            foreach (var word in ManagerForbidden)
            {
                Assert.False(html.Contains(word, StringComparison.OrdinalIgnoreCase), $"{url} contains \"{word}\"");
            }
        }

        // The Admin sees billing on the run and line pages...
        var adminRun = await admin.GetStringAsync($"/payroll/{second}");
        Assert.Contains("data-testid=\"total-invoice\"", adminRun);
        Assert.Contains("$429.09", adminRun); // 409.09 + 20 bonus
        Assert.Contains("data-testid=\"line-billing\"", await admin.GetStringAsync($"/payroll/{second}/lines/{bilal}"));

        // ...but never on a payslip.
        foreach (var url in new[] { $"/payroll/{second}/lines/{bilal}/payslip", $"/payroll/{second}/payslips" })
        {
            var html = WebUtility.HtmlDecode(await admin.GetStringAsync(url));
            foreach (Match slip in PayslipRegex().Matches(html))
            {
                foreach (var word in ManagerForbidden)
                {
                    Assert.False(slip.Value.Contains(word, StringComparison.OrdinalIgnoreCase), $"Admin payslip {url} contains \"{word}\"");
                }
            }
        }

        var payslip = WebUtility.HtmlDecode(await manager.GetStringAsync($"/payroll/{second}/lines/{bilal}/payslip"));
        Assert.Contains("Rs 85,782", payslip); // 80,182 + 5,600
        Assert.Contains("1 USD = Rs 280.00", payslip);
        Assert.Contains("data-testid=\"payslip-issuer\">HR Payroll<", payslip);

        var register = WebUtility.HtmlDecode(await manager.GetStringAsync($"/payroll/{first}/register"));
        Assert.Equal(6, Regex.Matches(register, "data-testid=\"register-row\"").Count);
        Assert.Contains("data-testid=\"register-total\"><strong>Rs 413,000<", register); // 42,000 + 22,910 + 40,090 + 98,000 + 168,000 + 42,000
    }

    // ===================== Security =====================

    [Fact]
    public async Task Payroll_mutations_are_POST_only_need_antiforgery_and_are_audited()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Audit Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, OwnerPay());
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        var line = (await LinesAsync(run)).Single().Id;

        await admin.PostFormAsync($"/payroll/{run}/lines/{line}/adjustments", $"/payroll/{run}/lines/{line}",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "5000", ["Currency"] = "PKR", ["Note"] = "Secret note 9Z" });
        var adjustment = (await LinesAsync(run)).Single().Adjustments.Single();
        await admin.PostFormAsync($"/payroll/{run}/lines/{line}/adjustments/{adjustment.Id}/edit", $"/payroll/{run}/lines/{line}/adjustments/{adjustment.Id}/edit",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "6000", ["Currency"] = "PKR", ["Note"] = "Secret note 9Z", ["RowVersion"] = Convert.ToBase64String(adjustment.RowVersion) });
        await admin.PostFormAsync($"/payroll/{run}/lines/{line}/extra-days", $"/payroll/{run}/lines/{line}", new Dictionary<string, string> { ["ExtraDays"] = "1", ["ExtraDaysNote"] = "Secret note 9Z" });
        await admin.PostFormAsync($"/payroll/{run}/rate", $"/payroll/{run}", new Dictionary<string, string> { ["Source"] = "override", ["Rate"] = "281", ["RateNote"] = "Secret note 9Z" });
        await admin.PostFormAsync($"/payroll/{run}/lines/{line}/adjustments/{adjustment.Id}/delete", $"/payroll/{run}/lines/{line}");
        await admin.PostFormAsync($"/payroll/{run}/regenerate", $"/payroll/{run}");
        await FinalizeAsync(admin, run);

        var entries = App.Logs.Entries.Where(e => e.Values.Contains($"RunId={run}")).ToList();
        foreach (var eventId in new[] { 1500, 1501, 1502, 1503, 1504, 1505, 1506, 1507 })
        {
            Assert.Contains(entries, e => e.EventId.Id == eventId && e.Values.Contains($"ActorId={adminUser.Id}"));
        }

        Assert.Contains(entries, e => e.EventId.Id == 1505 && e.Values.Contains("OldValues=Bonus 5000 PKR") && e.Values.Contains("NewValues=Bonus 6000 PKR"));
        Assert.Contains(entries, e => e.EventId.Id == 1502 && e.Values.Contains("OldRate=280.0000") && e.Values.Contains("NewRate=281.0000"));
        Assert.Contains(entries, e => e.EventId.Id == 1503 && e.Values.Contains($"LineId={line}") && e.Values.Contains("NewDays=1"));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("Secret note", StringComparison.Ordinal));
        Assert.All(entries, e => Assert.Equal(SecurityLog.Category, e.Category));

        foreach (var url in new[] { $"/payroll/{run}/finalize", $"/payroll/{run}/delete", $"/payroll/{run}/regenerate" })
        {
            var get = await admin.GetAsync(url);
            Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"GET {url} → {(int)get.StatusCode}");
        }

        foreach (var url in new[]
                 {
                     "/payroll/generate", $"/payroll/{run}/regenerate", $"/payroll/{run}/rate", $"/payroll/{run}/finalize", $"/payroll/{run}/reopen",
                     $"/payroll/{run}/delete", $"/payroll/{run}/lines/{line}/extra-days", $"/payroll/{run}/lines/{line}/adjustments",
                 })
        {
            var post = await admin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["Reason"] = "a long enough reason" }));
            Assert.True(post.StatusCode == HttpStatusCode.BadRequest, $"POST {url} without a token → {(int)post.StatusCode}");
        }

        using var anonymous = App.CreateHttpsClient();
        foreach (var url in new[] { "/payroll", $"/payroll/{run}", $"/payroll/{run}/register", $"/payroll/{run}/lines/{line}/payslip" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("/account/login", response.Headers.Location?.PathAndQueryOrOriginal(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Payroll_pages_have_no_inline_script_or_style_and_the_print_stylesheet_is_served()
    {
        var g = await SeedGoldenAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(16));
        var line = (await LinesAsync(run)).Single(l => l.PersonId == g.Bilal).Id;
        await admin.PostFormAsync($"/payroll/{run}/lines/{line}/adjustments", $"/payroll/{run}/lines/{line}",
            new Dictionary<string, string> { ["Type"] = "Deduction", ["Amount"] = "1000", ["Currency"] = "PKR" });
        var adjustment = (await LinesAsync(run)).Single(l => l.Id == line).Adjustments.Single().Id;

        foreach (var url in new[]
                 {
                     "/payroll", $"/payroll/{run}", $"/payroll/{run}/lines/{line}", $"/payroll/{run}/lines/{line}/adjustments/{adjustment}/edit",
                     $"/payroll/{run}/lines/{line}/payslip", $"/payroll/{run}/payslips", $"/payroll/{run}/register",
                 })
        {
            var response = await admin.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} → {(int)response.StatusCode}");
            var html = await response.Content.ReadAsStringAsync();
            Assert.Empty(InlineScriptRegex().Matches(html));
            Assert.DoesNotContain(" style=\"", html);
            Assert.Empty(EventHandlerRegex().Matches(html));
            Assert.Single(Regex.Matches(html, "<h1[\\s>]"));
            Assert.Contains("href=\"/css/print.css", html);
            Assert.Contains("media=\"print\"", html);
        }

        using var anonymous = App.CreateHttpsClient();
        var css = await anonymous.GetAsync("/css/print.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        var text = await css.Content.ReadAsStringAsync();
        Assert.Contains("size: A4", text);
        Assert.Contains(".aurora", text);
        Assert.Contains("break-before: page", text);
    }

    [GeneratedRegex("class=\"toast-message\"[^>]*>([^<]*)<")]
    private static partial Regex ToastRegex();

    [GeneratedRegex("<article class=\"payslip[\\s\\S]*?</article>")]
    private static partial Regex PayslipRegex();

    [GeneratedRegex("<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex("\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();
}
