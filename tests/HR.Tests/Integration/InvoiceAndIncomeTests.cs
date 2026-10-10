using System.Net;
using System.Text.RegularExpressions;
using HR.Domain.Absences;
using HR.Domain.Invoices;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Settings;
using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Invoices;
using HR.Infrastructure.Pay;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Settings;
using HR.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HR.Tests.Integration;

/// <summary>M8: Owner earning, negative net pay, late additions, settings, company invoices and owner income.</summary>
public partial class InvoiceAndIncomeTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private HrWebApplicationFactory App => Fixture.Development;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static readonly DateOnly LongAgo = new(2025, 1, 6);

    // ---------- Setup ----------

    private sealed record Golden(int Ayesha, int Nadia, int Kamran, int Bilal, int Imran, int Rizwan);

    private async Task AddRateAsync(DateOnly from, decimal rate)
    {
        await using var scope = App.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<ExchangeRateService>().CreateAsync(new RateInput(from, rate, null), true, "test")).Succeeded);
    }

    private async Task PayAsync(int personId, AdminPayInput input)
    {
        await using var scope = App.Services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<PayRecordService>().CreateAdminAsync(personId, input, null, confirmLoss: true, "test")).Succeeded);
    }

    private static async Task AbsentAsync(int personId, DateOnly date, AbsencePortion portion = AbsencePortion.Full)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        db.Absences.Add(Absence.Create(personId, date, portion, null, "test", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<Golden> SeedGoldenAsync()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var ayesha = await App.CreatePersonAsync("Ayesha Golden", joined: LongAgo, source: HireSource.CompanyRecommended, designation: "Engineer");
        var nadia = await App.CreatePersonAsync("Nadia Golden", joined: Oct(8), source: HireSource.CompanyRecommended);
        var kamran = await App.CreatePersonAsync("Kamran Golden", joined: LongAgo, source: HireSource.CompanyRecommended);
        var bilal = await App.CreatePersonAsync("Bilal Golden", joined: LongAgo, source: HireSource.BudgetHire, designation: "QA Engineer");
        var imran = await App.CreatePersonAsync("Imran Golden", joined: LongAgo, source: HireSource.Owner, designation: "Lead");
        var rizwan = await App.CreatePersonAsync("Rizwan Golden", joined: LongAgo, source: HireSource.CompanyRecommended, left: Oct(21));
        foreach (var id in new[] { ayesha, nadia, kamran, rizwan })
        {
            await PayAsync(id, new AdminPayInput(Oct(1), 300m, 25m, null, null, null));
        }

        await PayAsync(bilal, new AdminPayInput(Oct(1), null, null, 1000m, 196_000m, PayCurrency.PKR));
        await PayAsync(imran, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        await AbsentAsync(kamran, Oct(5), AbsencePortion.Half);
        await AbsentAsync(kamran, Oct(7));
        await AbsentAsync(bilal, Oct(6));
        await AbsentAsync(bilal, Oct(20));
        await AbsentAsync(bilal, Oct(27));
        return new Golden(ayesha, nadia, kamran, bilal, imran, rizwan);
    }

    private async Task CompleteSettingsAsync(string prefix = "INV", int terms = 7)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var current = await service.GetAsync();
        var result = await service.UpdateAsync(new SettingsInput("Tower Staffing", "1 Main Street\nKarachi", "billing@tower.example", "+92 21 1111111",
            "Meezan Bank", "Tower Staffing", "PK36SCBL0000001123456702", "MEZNPKKA", "Client Corp", "9 Client Road\nDubai", "Sara Khan", "ap@client.example",
            prefix, terms, "Thank you for your business.", "Tower Staffing"), current.RowVersion, "test");
        Assert.True(result.Succeeded);
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

    private static Task<HttpResponseMessage> FinalizeAsync(HttpClient client, int runId) =>
        client.PostFormAsync($"/payroll/{runId}/finalize", $"/payroll/{runId}");

    private static async Task<List<Invoice>> InvoicesAsync(int? runId = null)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => runId == null || i.RunId == runId).OrderBy(i => i.Id).ToListAsync();
    }

    private static async Task<List<PayrollLine>> LinesAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollLines.AsNoTracking().Where(l => l.RunId == runId).ToListAsync();
    }

    private static string? Toast(string html) => ToastRegex().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;

    private static string RowVersionOf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"(?:RowVersion|rowVersion)\" value=\"([^\"]*)\"").Groups[1].Value);

    private sealed class FixedClock(DateOnly today) : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(today.ToDateTime(new TimeOnly(5, 0)), TimeSpan.Zero);

        public DateOnly Today { get; } = today;
    }

    private WebApplicationFactory<Program> AppAt(DateOnly today) =>
        App.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IClock>();
            s.AddSingleton<IClock>(new FixedClock(today));
        }));

    private async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory, string role)
    {
        var user = await App.CreateUserAsync(role);
        var client = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostLoginAsync(user.Email, user.Password)).StatusCode);
        return client;
    }

    // ===================== Part A =====================

    [Fact]
    public async Task Run_totals_include_the_Owner_line_whole_invoice()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var owner = await App.CreatePersonAsync("Owner Person", joined: LongAgo, source: HireSource.Owner);
        var cr = await App.CreatePersonAsync("Recommended Person", joined: LongAgo, source: HireSource.CompanyRecommended);
        await PayAsync(owner, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        await PayAsync(cr, new AdminPayInput(Oct(1), 300m, 25m, null, null, null));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);

        var run = await GenerateAsync(admin, Oct(1));
        var html = WebUtility.HtmlDecode(await admin.GetStringAsync($"/payroll/{run}"));
        Assert.Matches("data-testid=\"total-earning\">[\\s\\S]*?\\$625\\.00", html); // 600.00 (Owner) + 25.00
        Assert.Contains("Rs 175,000", html); // 168,000 + 7,000
        var ownerLine = (await LinesAsync(run)).Single(l => l.PersonId == owner);
        Assert.Equal((600.00m, 168_000m), (ownerLine.OwnerEarningUsd!.Value, ownerLine.OwnerEarningPkr!.Value));

        var linePage = WebUtility.HtmlDecode(await admin.GetStringAsync($"/payroll/{run}/lines/{ownerLine.Id}"));
        Assert.Contains("your whole invoiced amount", linePage);
        Assert.Contains("Your whole billed amount", WebUtility.HtmlDecode(await admin.GetStringAsync($"/people/{owner}")));
    }

    [Fact]
    public async Task Deductions_that_exceed_pay_block_finalize()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Deducted Person", joined: LongAgo, source: HireSource.CompanyRecommended);
        await PayAsync(id, new AdminPayInput(Oct(1), 300m, 25m, null, null, null));
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var run = await GenerateAsync(manager, Oct(1));
        var line = (await LinesAsync(run)).Single().Id;

        await manager.PostFormAsync($"/payroll/{run}/lines/{line}/adjustments", $"/payroll/{run}/lines/{line}",
            new Dictionary<string, string> { ["Type"] = "Deduction", ["Amount"] = "50000", ["Currency"] = "PKR" });
        var stored = (await LinesAsync(run)).Single();
        Assert.Equal((-8_000m, LineIssue.NegativeNetPay), (stored.NetPayPkr!.Value, stored.Issue));
        Assert.Contains("Deductions exceed pay", await manager.GetStringAsync($"/payroll/{run}"));
        Assert.Contains(WebUtility.HtmlEncode(HR.Infrastructure.Payroll.PayrollService.IssuesMessage), await manager.FollowAsync(await FinalizeAsync(manager, run)));
        Assert.Equal(PayrollStatus.Draft, (await RunStatusAsync(run)));
    }

    private static async Task<PayrollStatus> RunStatusAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollRuns.Where(r => r.Id == runId).Select(r => r.Status).SingleAsync();
    }

    [Fact]
    public async Task People_added_into_a_finalized_period_need_a_confirmation_that_is_audited()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var existing = await App.CreatePersonAsync("Existing Owner", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(existing, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        var leaver = await App.CreatePersonAsync("Earlier Leaver", joined: LongAgo, left: new DateOnly(2026, 9, 25));
        var later = await App.CreatePersonAsync("Later Joiner", joined: Oct(19));
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, run);
        var expected = "This person is not included in the finalized payroll(s) for 01–15 Oct 2026. Pay any arrears as a Bonus or extra days in the current payroll.";

        // 1. Creating someone who joined Oct 5.
        var form = PeopleHelpers.Form("Backdated Person", joined: "2026-10-05");
        var refused = await admin.PostFormAsync("/people/create", "/people/create", form);
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var page = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        Assert.Contains("data-testid=\"late-addition\"", page);
        Assert.Contains(expected, page);
        form["ConfirmLateAddition"] = "true";
        var created = await admin.PostFormAsync("/people/create", "/people/create", form);
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var newId = PeopleHelpers.IdFromLocation(created);
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1106 && e.Values.Contains($"PersonId={newId}") && e.Values.Contains($"ActorId={adminUser.Id}") && e.Values.Contains("Periods=01–15 Oct 2026"));

        // 2. Reactivating into the finalized period (the leaver isn't in that payroll).
        await using (var scope = App.Services.CreateAsyncScope())
        {
            var people = scope.ServiceProvider.GetRequiredService<PersonService>();
            var rejoin = await people.ReactivateAsync(leaver, Oct(5), "t", revealHireSource: true);
            Assert.Equal((PersonService.ConfirmLateAdditionField, expected), (rejoin.Errors![0].Field, rejoin.Errors[0].Message));
            Assert.True((await people.ReactivateAsync(leaver, Oct(5), "t", revealHireSource: true, confirmLateAddition: true)).Succeeded);

            // 3. Moving a joining date earlier into it.
            var details = await people.GetAsync(later);
            var input = new PersonInput(details!.FullName, details.Type, details.Designation, null, details.Phone, null, null, null, Oct(5), null);
            Assert.Equal(PersonService.ConfirmLateAdditionField, (await people.UpdateAsync(later, input, true, true, details.RowVersion, "t")).Errors![0].Field);
            Assert.True((await people.UpdateAsync(later, input, true, true, details.RowVersion, "t", confirmLateAddition: true)).Succeeded);

            // Someone who IS in the finalized payroll still can't have their days changed (M7 rule).
            var inside = await people.DeactivateAsync(existing, Oct(9), "t");
            Assert.Contains("would alter the finalized payroll", inside.Errors![0].Message);
        }

        Assert.Equal(3, App.Logs.Entries.Count(e => e.EventId.Id == 1106));
        Assert.Single(await LinesAsync(run)); // the finalized payroll itself is unchanged
    }

    // ===================== Part B: Settings =====================

    [Fact]
    public async Task Settings_validate_audit_field_names_only_and_detect_concurrent_edits()
    {
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        var page = await admin.GetStringAsync("/admin/settings");
        Assert.Contains("data-testid=\"settings-incomplete\"", page);
        var rowVersion = RowVersionOf(page);

        Dictionary<string, string> Form(string email) => new()
        {
            ["BusinessName"] = "Tower Staffing", ["BusinessEmail"] = email, ["ClientName"] = "Client Corp", ["BankAccountNumber"] = "PK36 SCBL 0000 0011 2345 6702",
            ["InvoicePrefix"] = "twr", ["PaymentTermsDays"] = "10", ["PayslipIssuerName"] = "Tower Staffing", ["RowVersion"] = rowVersion,
        };

        var invalid = await admin.PostFormAsync("/admin/settings", "/admin/settings", Form("not-an-email"));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Enter a valid email address.", await invalid.Content.ReadAsStringAsync());

        var saved = await admin.PostFormAsync("/admin/settings", "/admin/settings", Form("Billing@Tower.example"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var s = await db.Settings.SingleAsync();
            Assert.Equal(("TWR", 10, "billing@tower.example", "PK36SCBL0000001123456702", "Tower Staffing"), (s.InvoicePrefix, s.PaymentTermsDays, s.BusinessEmail, s.BankAccountNumber, s.PayslipIssuerName));
        }

        var audit = App.Logs.Entries.Single(e => e.EventId.Id == 1600 && e.Values.Contains($"ActorId={adminUser.Id}"));
        Assert.Contains("BusinessName", audit.AllText);
        Assert.Contains("ClientName", audit.AllText);
        Assert.DoesNotContain("Tower Staffing", audit.AllText);
        Assert.DoesNotContain("PK36", audit.AllText);
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("billing@tower.example", StringComparison.OrdinalIgnoreCase));

        // The old row version is stale now: a second editor gets a 409 and their changes are not saved.
        var stale = await admin.PostFormAsync("/admin/settings", "/admin/settings", Form("other@tower.example"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("Someone else saved the settings", WebUtility.HtmlDecode(await stale.Content.ReadAsStringAsync()));
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            Assert.Equal("billing@tower.example", (await db.Settings.SingleAsync()).BusinessEmail);
        }

        // The payslip issuer now comes from Settings.
        Assert.DoesNotContain("data-testid=\"settings-incomplete\"", await admin.GetStringAsync("/admin/settings"));
    }

    // ===================== Part C: Invoices =====================

    private static readonly string[] InvoiceForbidden = ["commission", "margin", "budget", "earning"];
    private static readonly string[] HireSourceNames = ["Company recommended", "CompanyRecommended", "Budget hire", "BudgetHire", "Owner"];

    [Fact]
    public async Task Finalizing_issues_the_invoice_with_one_line_per_person_and_the_total_of_InvoiceUsd()
    {
        var g = await SeedGoldenAsync();
        await CompleteSettingsAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var first = await GenerateAsync(admin, Oct(1));
        var second = await GenerateAsync(admin, Oct(16));
        var bilalLine = (await LinesAsync(second)).Single(l => l.PersonId == g.Bilal).Id;
        foreach (var (type, amount, currency) in new[] { ("Reimbursement", "5000", "PKR"), ("Bonus", "20", "USD"), ("Deduction", "1000", "PKR") })
        {
            await admin.PostFormAsync($"/payroll/{second}/lines/{bilalLine}/adjustments", $"/payroll/{second}/lines/{bilalLine}",
                new Dictionary<string, string> { ["Type"] = type, ["Amount"] = amount, ["Currency"] = currency });
        }

        await FinalizeAsync(admin, first);
        await FinalizeAsync(admin, second);

        var invoices = await InvoicesAsync();
        Assert.Equal(["INV-2026-0001", "INV-2026-0002"], invoices.Select(i => i.Number).ToArray());
        var inv = invoices[1];
        var lines = await LinesAsync(second);
        Assert.Equal(lines.Sum(l => l.InvoiceUsd!.Value), inv.TotalUsd);
        Assert.Equal(6, inv.Lines.Count);
        Assert.Equal(lines.Select(l => l.PersonName).Order(StringComparer.OrdinalIgnoreCase).ToArray(), inv.Lines.OrderBy(l => l.Position).Select(l => l.Name).ToArray());
        var bilal = inv.Lines.Single(l => l.PersonId == g.Bilal);
        Assert.Equal(("9/11", 409.09m, 34.29m, 443.38m, "QA Engineer"), (bilal.DaysText, bilal.SalaryUsd, bilal.ExtrasUsd, bilal.AmountUsd, bilal.Designation));
        var owner = inv.Lines.Single(l => l.PersonId == g.Imran);
        Assert.Equal(600.00m, owner.AmountUsd);
        Assert.Equal((InvoiceStatus.Issued, InvoiceMath.DueDate(inv.IssueDate, 7), "Tower Staffing", "Client Corp"), (inv.Status, inv.DueDate, inv.BusinessName, inv.ClientName));
        Assert.Equal(PakistanTime.ToKarachiDate((await RunAsync(second)).FinalizedAt!.Value), inv.IssueDate);

        // The page: totals, the Owner line, and none of the forbidden words anywhere on it.
        var html = await admin.GetStringAsync($"/invoices/{inv.Id}");
        var text = WebUtility.HtmlDecode(html);
        Assert.Contains("Imran Golden", text);
        Assert.Contains($"data-testid=\"invoice-total\"><strong>{HR.Web.Formatting.DisplayFormat.Usd(inv.TotalUsd)}<", text);
        Assert.Contains("$443.38", text);
        Assert.Contains("$34.29", text);
        foreach (var word in InvoiceForbidden)
        {
            Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"The invoice page contains \"{word}\"");
        }

        var document = InvoiceDocumentRegex().Match(text).Value;
        Assert.NotEmpty(document);
        foreach (var name in HireSourceNames)
        {
            Assert.False(document.Contains(name, StringComparison.OrdinalIgnoreCase), $"The invoice contains \"{name}\"");
        }

        Assert.Contains("href=\"/css/print.css", html);
        Assert.Contains($"href=\"/invoices/{inv.Id}\"", await admin.GetStringAsync($"/payroll/{second}"));
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1601 && e.Values.Contains($"InvoiceId={inv.Id}"));
    }

    private static async Task<PayrollRun> RunAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    [Fact]
    public async Task Without_settings_the_invoice_is_pending_and_can_be_issued_later()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Pending Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        Assert.Equal("Payroll finalized. Its figures are now locked.", Toast(await admin.FollowAsync(await FinalizeAsync(admin, run))));
        Assert.Empty(await InvoicesAsync());

        var runPage = await admin.GetStringAsync($"/payroll/{run}");
        Assert.Contains("data-testid=\"invoice-pending\"", runPage);
        Assert.Contains("href=\"/admin/settings\"", runPage);
        var list = await admin.GetStringAsync("/invoices");
        Assert.Contains("data-testid=\"settings-incomplete\"", list);
        Assert.Contains("href=\"/admin/settings\"", list);
        var early = await admin.PostFormAsync($"/invoices/issue/{run}", $"/payroll/{run}");
        Assert.Contains("Invoice pending: complete Settings", WebUtility.HtmlDecode(await admin.FollowAsync(early)));

        await CompleteSettingsAsync(prefix: "TWR");
        Assert.Contains("data-testid=\"issue-invoice\"", await admin.GetStringAsync($"/payroll/{run}"));
        Assert.DoesNotContain("data-testid=\"settings-incomplete\"", await admin.GetStringAsync("/invoices"));
        var issued = await admin.PostFormAsync($"/invoices/issue/{run}", $"/payroll/{run}");
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        var invoice = Assert.Single(await InvoicesAsync());
        Assert.Equal(("TWR-2026-0001", 600.00m), (invoice.Number, invoice.TotalUsd));
        Assert.Equal(PakistanTime.ToKarachiDate((await RunAsync(run)).FinalizedAt!.Value), invoice.IssueDate); // dated on the finalize date

        var again = await admin.PostFormAsync($"/invoices/issue/{run}", $"/payroll/{run}");
        Assert.Contains("already has an invoice", await admin.FollowAsync(again));
        Assert.Single(await InvoicesAsync());
    }

    [Fact]
    public async Task Reopen_voids_the_invoice_refinalize_replaces_it_and_paid_invoices_block_reopen()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Reopen Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        await CompleteSettingsAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, run);
        var original = Assert.Single(await InvoicesAsync());

        // Paid: reopen refused.
        var page = await admin.GetStringAsync($"/invoices/{original.Id}");
        var paid = await admin.PostFormAsync($"/invoices/{original.Id}/paid", $"/invoices/{original.Id}", new Dictionary<string, string>
        {
            ["PaidDate"] = PeopleHelpers.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["AmountReceivedUsd"] = "600.00", ["PaymentNote"] = "Wire 123", ["RowVersion"] = RowVersionOf(page),
        });
        Assert.Equal("Invoice marked paid.", Toast(await admin.FollowAsync(paid)));
        var reason = new Dictionary<string, string> { ["Reason"] = "Bonus was missing for the owner" };
        var refused = await admin.PostFormAsync($"/payroll/{run}/reopen", $"/payroll/{run}", reason);
        Assert.Contains(InvoiceService.PaidReopenMessage, await admin.FollowAsync(refused));
        Assert.Equal(PayrollStatus.Finalized, await RunStatusAsync(run));

        // DB level: a paid invoice's lines can't change.
        await using (var db = TestDatabaseFixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [InvoiceLines] SET [AmountUsd] = 1 WHERE [InvoiceId] = {original.Id}"));
            Assert.Equal(51020, ex.Number);
        }

        // Unpaid, then reopen voids it with the reason; re-finalize issues a replacement.
        page = await admin.GetStringAsync($"/invoices/{original.Id}");
        await admin.PostFormAsync($"/invoices/{original.Id}/unpaid", $"/invoices/{original.Id}", new Dictionary<string, string> { ["rowVersion"] = RowVersionOf(page) });
        Assert.Equal(InvoiceStatus.Issued, (await InvoicesAsync()).Single().Status);
        await admin.PostFormAsync($"/payroll/{run}/reopen", $"/payroll/{run}", reason);
        var voided = (await InvoicesAsync()).Single();
        Assert.Equal((InvoiceStatus.Void, "Bonus was missing for the owner"), (voided.Status, voided.VoidReason));

        var line = (await LinesAsync(run)).Single().Id;
        await admin.PostFormAsync($"/payroll/{run}/lines/{line}/adjustments", $"/payroll/{run}/lines/{line}",
            new Dictionary<string, string> { ["Type"] = "Bonus", ["Amount"] = "50", ["Currency"] = "USD" });
        await FinalizeAsync(admin, run);
        var all = await InvoicesAsync();
        Assert.Equal(2, all.Count);
        var replacement = all[1];
        Assert.Equal(("INV-2026-0002", original.Id, 650.00m, InvoiceStatus.Issued), (replacement.Number, replacement.ReplacesInvoiceId!.Value, replacement.TotalUsd, replacement.Status));

        var voidPage = await admin.GetStringAsync($"/invoices/{original.Id}");
        Assert.Contains("data-testid=\"void-banner\"", voidPage);
        Assert.Contains($"href=\"/invoices/{replacement.Id}\"", voidPage);
        Assert.Contains("Replaces INV-2026-0001", await admin.GetStringAsync($"/invoices/{replacement.Id}"));

        // A draft that had an invoice is kept for the record.
        Assert.Contains(App.Logs.Entries, e => e.EventId.Id == 1604 && e.Values.Contains($"InvoiceId={original.Id}"));
        Assert.DoesNotContain(App.Logs.Entries, e => e.AllText.Contains("Bonus was missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overdue_status_outstanding_totals_and_the_dashboard_follow_the_clock()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Overdue Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        await CompleteSettingsAsync(terms: 7);

        int invoiceId;
        await using (var issueDay = AppAt(Oct(15)))
        {
            var admin = await SignInAsync(issueDay, AppRoles.Admin);
            var run = await GenerateAsync(admin, Oct(1));
            await FinalizeAsync(admin, run);
            var invoice = Assert.Single(await InvoicesAsync());
            invoiceId = invoice.Id;
            Assert.Equal((Oct(15), Oct(22)), (invoice.IssueDate, invoice.DueDate));

            var list = await admin.GetStringAsync("/invoices");
            Assert.Contains("data-testid=\"invoice-status\">Issued<", list);
            Assert.Matches("data-testid=\"total-outstanding\"[\\s\\S]*?stat-value\">\\$600\\.00<", list);
            Assert.Matches("data-testid=\"invoices-tile\"[\\s\\S]*?stat-value\">\\$600\\.00<[\\s\\S]*?1 unpaid · 0 overdue", WebUtility.HtmlDecode(await admin.GetStringAsync("/")));
        }

        await using (var late = AppAt(Oct(23)))
        {
            var admin = await SignInAsync(late, AppRoles.Admin);
            Assert.Contains("data-testid=\"invoice-status\">Overdue<", await admin.GetStringAsync("/invoices"));
            Assert.Contains("data-testid=\"invoice-row\"", await admin.GetStringAsync("/invoices?status=Overdue"));
            Assert.Matches("data-testid=\"invoices-tile\"[\\s\\S]*?1 unpaid · 1 overdue", WebUtility.HtmlDecode(await admin.GetStringAsync("/")));

            // Payment validation, then paid: received and outstanding totals move.
            var page = await admin.GetStringAsync($"/invoices/{invoiceId}");
            var future = await admin.PostFormAsync($"/invoices/{invoiceId}/paid", $"/invoices/{invoiceId}", new Dictionary<string, string>
            {
                ["PaidDate"] = "2026-10-30", ["AmountReceivedUsd"] = "600", ["RowVersion"] = RowVersionOf(page),
            });
            Assert.Contains("between the issue date and today", await future.Content.ReadAsStringAsync());

            await admin.PostFormAsync($"/invoices/{invoiceId}/paid", $"/invoices/{invoiceId}", new Dictionary<string, string>
            {
                ["PaidDate"] = "2026-10-23", ["AmountReceivedUsd"] = "595.50", ["RowVersion"] = RowVersionOf(page),
            });
            var list = await admin.GetStringAsync("/invoices");
            Assert.Contains("data-testid=\"invoice-status\">Paid<", list);
            Assert.Matches("data-testid=\"total-received\"[\\s\\S]*?stat-value\">\\$595\\.50<", list);
            Assert.Matches("data-testid=\"total-outstanding\"[\\s\\S]*?stat-value\">\\$0\\.00<", list);
            Assert.Matches("data-testid=\"total-invoiced\"[\\s\\S]*?stat-value\">\\$600\\.00<", list);
            Assert.Empty(InvoiceRowRegex().Matches(await admin.GetStringAsync("/invoices?status=Issued")));
            Assert.Single(InvoiceRowRegex().Matches(await admin.GetStringAsync("/invoices?year=2026")));
            Assert.Empty(InvoiceRowRegex().Matches(await admin.GetStringAsync("/invoices?year=2025")));
        }
    }

    // ===================== Part D: Owner income =====================

    [Fact]
    public async Task Owner_income_totals_match_the_frozen_lines_by_period_month_and_year_with_an_optional_draft()
    {
        var g = await SeedGoldenAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var first = await GenerateAsync(admin, Oct(1));
        var second = await GenerateAsync(admin, Oct(16));
        await FinalizeAsync(admin, first);
        await FinalizeAsync(admin, second);
        var lines1 = await LinesAsync(first);
        var lines2 = await LinesAsync(second);

        static string Usd(decimal v) => HR.Web.Formatting.DisplayFormat.Usd(v);
        static string Pkr(decimal v) => HR.Web.Formatting.DisplayFormat.Pkr(v);
        string Total(string html) => Regex.Match(WebUtility.HtmlDecode(html), "data-testid=\"selection-total-usd\"><strong>([^<]*)<").Groups[1].Value;

        var period = await admin.GetStringAsync("/owner-income?view=Period&at=2026-10-16");
        Assert.Equal(Usd(lines2.Sum(l => l.OwnerEarningUsd!.Value)), Total(period));
        Assert.Equal(Usd(806.82m), Total(period)); // 25 + 25 + 25 + 122.73 + 600 + 9.09
        Assert.Contains($"data-testid=\"selection-total-pkr\">{Pkr(lines2.Sum(l => l.OwnerEarningPkr!.Value))}<", WebUtility.HtmlDecode(period));

        var month = await admin.GetStringAsync("/owner-income?view=Month&at=2026-10-01");
        var monthTotal = lines1.Concat(lines2).Sum(l => l.OwnerEarningUsd!.Value);
        Assert.Equal(Usd(monthTotal), Total(month));
        Assert.Equal(Usd(1_644.32m), Total(month)); // 837.50 + 806.82
        Assert.Equal(Usd(monthTotal), Total(await admin.GetStringAsync("/owner-income?view=Year&at=2026-01-01")));
        Assert.Equal(Usd(0m), Total(await admin.GetStringAsync("/owner-income?view=Year&at=2025-01-01")));

        // Breakdown, contributors, chart table.
        var decoded = WebUtility.HtmlDecode(month);
        Assert.Contains("Own salary", decoded);
        Assert.Contains(Usd(1_200m), decoded); // Imran, two periods
        Assert.Matches("data-testid=\"contributor-row\"[\\s\\S]*?Imran Golden", decoded);
        Assert.Equal(6, ContributorRegex().Matches(month).Count);
        Assert.Contains("<svg class=\"income-chart\"", month);
        Assert.Contains("<title id=\"chartTitle\">", month);
        Assert.Equal(12, Regex.Matches(month, "data-testid=\"chart-row\"").Count);

        // A draft is shown separately only when asked for.
        await GenerateAsync(admin, new DateOnly(2026, 11, 1));
        var nov = await admin.GetStringAsync("/owner-income?view=Month&at=2026-11-01");
        Assert.Equal(Usd(0m), Total(nov));
        Assert.DoesNotContain("selection-draft-usd", nov);
        var withDraft = WebUtility.HtmlDecode(await admin.GetStringAsync("/owner-income?view=Month&at=2026-11-01&draft=true"));
        Assert.Equal(Usd(0m), Total(withDraft));
        Assert.Matches("data-testid=\"selection-draft-usd\">\\$[1-9]", withDraft);
        Assert.Contains("Draft (projected)", withDraft);
    }

    // ===================== Security =====================

    [Fact]
    public async Task Managers_get_403_on_every_invoice_income_and_settings_route()
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        var id = await App.CreatePersonAsync("Guarded Person", joined: LongAgo, source: HireSource.Owner);
        await PayAsync(id, new AdminPayInput(Oct(1), 1200m, null, null, null, null));
        await CompleteSettingsAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, run);
        var invoice = Assert.Single(await InvoicesAsync());
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        foreach (var url in new[] { "/invoices", $"/invoices/{invoice.Id}", "/owner-income", "/owner-income?view=Year", "/admin/settings" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
        }

        foreach (var url in new[] { $"/invoices/{invoice.Id}/paid", $"/invoices/{invoice.Id}/unpaid", $"/invoices/issue/{run}", "/admin/settings" })
        {
            var post = await manager.PostFormAsync(url, "/", new Dictionary<string, string> { ["AmountReceivedUsd"] = "1", ["PaidDate"] = "2026-10-10" });
            Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        }

        Assert.Equal(InvoiceStatus.Issued, Assert.Single(await InvoicesAsync()).Status);
        var dashboard = await manager.GetStringAsync("/");
        Assert.DoesNotContain("invoices-tile", dashboard);
        Assert.DoesNotContain("/owner-income", dashboard);
        Assert.DoesNotContain("Invoice", await manager.GetStringAsync($"/payroll/{run}"));
    }

    [Fact]
    public async Task Mutations_are_POST_only_need_antiforgery_and_the_pages_have_no_inline_script_or_style()
    {
        var g = await SeedGoldenAsync();
        await CompleteSettingsAsync();
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var run = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, run);
        await GenerateAsync(admin, Oct(16));
        var invoice = Assert.Single(await InvoicesAsync());

        foreach (var url in new[] { $"/invoices/{invoice.Id}/paid", $"/invoices/{invoice.Id}/unpaid", $"/invoices/issue/{run}" })
        {
            var get = await admin.GetAsync(url);
            Assert.True(get.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"GET {url} → {(int)get.StatusCode}");
        }

        foreach (var url in new[] { $"/invoices/{invoice.Id}/paid", $"/invoices/{invoice.Id}/unpaid", $"/invoices/issue/{run}", "/admin/settings" })
        {
            var post = await admin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["BusinessName"] = "x" }));
            Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        }

        foreach (var url in new[] { "/invoices", $"/invoices/{invoice.Id}", "/owner-income", "/owner-income?view=Period&draft=true", "/owner-income?view=Year", "/admin/settings", "/" })
        {
            var html = await admin.GetStringAsync(url);
            Assert.Empty(InlineScriptRegex().Matches(html));
            Assert.DoesNotContain(" style=\"", html);
            Assert.Empty(EventHandlerRegex().Matches(html));
            Assert.Single(Regex.Matches(html, "<h1[\\s>]"));
        }

        using var anonymous = App.CreateHttpsClient();
        var css = await anonymous.GetStringAsync("/css/print.css");
        Assert.Contains(".invoice-doc", css);
        Assert.Contains(".invoice-table td::before", css);
        foreach (var url in new[] { "/invoices", "/owner-income", "/admin/settings" })
        {
            Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(url)).StatusCode);
        }
    }

    [GeneratedRegex("class=\"toast-message\"[^>]*>([^<]*)<")]
    private static partial Regex ToastRegex();

    [GeneratedRegex("<article class=\"invoice-doc[\\s\\S]*?</article>")]
    private static partial Regex InvoiceDocumentRegex();

    [GeneratedRegex("data-testid=\"invoice-row\"")]
    private static partial Regex InvoiceRowRegex();

    [GeneratedRegex("data-testid=\"contributor-row\"")]
    private static partial Regex ContributorRegex();

    [GeneratedRegex("<script\\b(?![^>]*\\bsrc\\s*=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex("\\son[a-z]+\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlerRegex();
}
