using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using HR.Domain.Absences;
using HR.Domain.Invoices;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Settings;
using HR.Domain.Time;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.Payroll;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Settings;
using HR.Tests.Integration.Infrastructure;
using HR.Web.Formatting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UglyToad.PdfPig;

namespace HR.Tests.Integration;

/// <summary>M9: short/over payments, Excel and PDF exports, reports, the Payslips tab and the dashboard trend.</summary>
public partial class ExportAndReportTests(TestDatabaseFixture fixture) : IntegrationTest(fixture)
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Pdf = "application/pdf";
    private const string Hyperlink = "=HYPERLINK(\"http://evil\",\"x\")";
    private const string AyeshaCnic = "35202-1234567-1";
    private const string AyeshaIban = "PK36SCBL0000001123456702";

    /// <summary>Words and names that must never reach a Manager's export.</summary>
    private static readonly string[] BillingWords =
        ["billed", "billing", "commission", "margin", "earning", "budget", "invoice", "hire source", "companyrecommended", "budgethire", "company recommended", "owner"];

    /// <summary>Billed-side amounts of the golden people that no pay-side figure equals (G1 billed, G4/G5 billed, the budget).</summary>
    private static readonly decimal[] BilledValues = [175.00m, 500.00m, 409.09m, 1000.00m];

    private HrWebApplicationFactory App => Fixture.Development;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static readonly DateOnly LongAgo = new(2025, 1, 6);

    // ===================== Setup =====================

    private sealed record Seeded(int Ayesha, int Nadia, int Kamran, int Bilal, int Imran, int Rizwan, int Evil, int Run1, int Run2);

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

    private async Task CompleteSettingsAsync()
    {
        await using var scope = App.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var current = await service.GetAsync();
        Assert.True((await service.UpdateAsync(new SettingsInput("Tower Staffing", "1 Main Street\nKarachi", "billing@tower.example", "+92 21 1111111",
            "Meezan Bank", "Tower Staffing", "PK36SCBL0000001123456702", "MEZNPKKA", "Client Corp", "9 Client Road\nDubai", "Sara Khan", "ap@client.example",
            "TWR", 7, "Thank you for your business.", "Tower Staffing"), current.RowVersion, "test")).Succeeded);
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

    private static async Task FinalizeAsync(HttpClient client, int runId)
    {
        await client.PostFormAsync($"/payroll/{runId}/finalize", $"/payroll/{runId}");
        await using var db = TestDatabaseFixture.CreateDbContext();
        Assert.Equal(PayrollStatus.Finalized, await db.PayrollRuns.Where(r => r.Id == runId).Select(r => r.Status).SingleAsync());
    }

    /// <summary>The golden people (SPEC §9) plus a formula-named person; two October payrolls, the second one only a draft unless asked.</summary>
    private async Task<Seeded> SeedAsync(HttpClient admin, bool finalizeSecond = true)
    {
        await AddRateAsync(new DateOnly(2026, 1, 1), 280m);
        await CompleteSettingsAsync();
        var ayesha = await App.CreatePersonAsync("Ayesha Golden", joined: LongAgo, source: HireSource.CompanyRecommended, cnic: AyeshaCnic, iban: AyeshaIban);
        var nadia = await App.CreatePersonAsync("Nadia Golden", joined: Oct(8), source: HireSource.CompanyRecommended);
        var kamran = await App.CreatePersonAsync("Kamran Golden", joined: LongAgo, source: HireSource.CompanyRecommended);
        var bilal = await App.CreatePersonAsync("Bilal Golden", joined: LongAgo, source: HireSource.BudgetHire, designation: "QA Engineer");
        var imran = await App.CreatePersonAsync("Imran Golden", joined: LongAgo, source: HireSource.Owner, designation: "Lead");
        var rizwan = await App.CreatePersonAsync("Rizwan Golden", joined: LongAgo, source: HireSource.CompanyRecommended, left: Oct(21));
        var evil = await App.CreatePersonAsync(Hyperlink, joined: LongAgo, source: HireSource.CompanyRecommended);
        foreach (var id in new[] { ayesha, nadia, kamran, rizwan, evil })
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

        var run1 = await GenerateAsync(admin, Oct(1));
        await FinalizeAsync(admin, run1);
        var run2 = await GenerateAsync(admin, Oct(16));
        if (finalizeSecond)
        {
            await FinalizeAsync(admin, run2);
        }

        return new Seeded(ayesha, nadia, kamran, bilal, imran, rizwan, evil, run1, run2);
    }

    private static async Task<List<PayrollLine>> LinesAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.PayrollLines.AsNoTracking().Where(l => l.RunId == runId && !l.IsOrphaned).ToListAsync();
    }

    private static async Task<Invoice> InvoiceForAsync(int runId)
    {
        await using var db = TestDatabaseFixture.CreateDbContext();
        return await db.Invoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.RunId == runId && i.Status != InvoiceStatus.Void);
    }

    // ===================== Download helpers =====================

    /// <summary>GETs a download and checks the headers every export must carry.</summary>
    private static async Task<(byte[] Bytes, string FileName)> DownloadAsync(HttpClient client, string url, string contentType)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode}");
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        var fileName = disposition.FileName!.Trim('"');
        Assert.Matches("^[A-Za-z0-9-]+\\.(xlsx|pdf)$", fileName);
        Assert.True(response.Headers.CacheControl?.NoStore, $"{url}: Cache-Control is {response.Headers.CacheControl}");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        return (await response.Content.ReadAsByteArrayAsync(), fileName);
    }

    /// <summary>A cell's value copied out of the workbook (cells can't be read once it is disposed).</summary>
    private sealed record CellValue(string Text, double? Number, DateTime? Date)
    {
        public string GetString() => Text;

        public double GetDouble() => Number ?? throw new InvalidOperationException($"Not a number: \"{Text}\"");

        public DateTime GetDateTime() => Date ?? throw new InvalidOperationException($"Not a date: \"{Text}\"");
    }

    private sealed record Sheet(string Name, List<string> Headers, List<List<CellValue>> Rows);

    /// <summary>Opens an .xlsx with ClosedXML: every sheet with its header and data rows (a totals row is a data row).</summary>
    private static List<Sheet> Open(byte[] bytes)
    {
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        return workbook.Worksheets.Select(ws =>
        {
            var used = ws.RangeUsed();
            var lastColumn = used?.LastColumn().ColumnNumber() ?? 0;
            var lastRow = used?.LastRow().RowNumber() ?? 0;
            var headers = Enumerable.Range(1, lastColumn).Select(c => ws.Cell(1, c).GetString()).ToList();
            var rows = Enumerable.Range(2, Math.Max(0, lastRow - 1))
                .Select(r => Enumerable.Range(1, lastColumn).Select(c => ws.Cell(r, c)).Select(c => new CellValue(c.GetString(),
                    c.DataType == XLDataType.Number ? c.GetDouble() : null,
                    c.DataType == XLDataType.DateTime ? c.GetDateTime() : null)).ToList())
                .ToList();
            return new Sheet(ws.Name, headers, rows);
        }).ToList();
    }

    /// <summary>Data rows of the first sheet, without a "Total…" row.</summary>
    private static List<List<CellValue>> DataRows(List<Sheet> sheets) =>
        sheets[0].Rows.Where(r => !r[0].GetString().StartsWith("Total", StringComparison.Ordinal)).ToList();

    private static CellValue Col(Sheet sheet, List<CellValue> row, string header) => row[sheet.Headers.IndexOf(header)];

    /// <summary>Every text and every number in a workbook (all sheets), lowercased text joined.</summary>
    private static (string Text, List<decimal> Numbers) Contents(byte[] xlsx)
    {
        using var workbook = new XLWorkbook(new MemoryStream(xlsx));
        var cells = workbook.Worksheets.SelectMany(ws => ws.CellsUsed()).ToList();
        return (string.Join(" | ", cells.Select(c => c.GetString())).ToLowerInvariant(),
            cells.Where(c => c.DataType == XLDataType.Number).Select(c => (decimal)c.GetDouble()).ToList());
    }

    private static (string Text, int Pages) PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var pages = document.GetPages().ToList();
        // Exact text: no normalisation (M10 fixed the dash substitution at the source).
        return (string.Join("\n", pages.Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))), pages.Count);
    }

    private static void AssertNoBillingData(string url, string text, IEnumerable<decimal> numbers)
    {
        var lower = text.ToLowerInvariant();
        foreach (var word in BillingWords)
        {
            Assert.False(lower.Contains(word, StringComparison.Ordinal), $"{url} contains \"{word}\"");
        }

        foreach (var value in BilledValues)
        {
            Assert.DoesNotContain(value, numbers);
        }
    }

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

    // ===================== Part A: short / exact / over payment =====================

    [Fact]
    public async Task Short_and_over_payments_show_pills_and_count_in_outstanding_and_excess()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin);
        var first = await InvoiceForAsync(s.Run1);
        var second = await InvoiceForAsync(s.Run2);

        async Task PayAsync(Invoice invoice, decimal amount)
        {
            var page = await admin.GetStringAsync($"/invoices/{invoice.Id}");
            var rowVersion = WebUtility.HtmlDecode(Regex.Match(page, "name=\"RowVersion\" value=\"([^\"]*)\"").Groups[1].Value);
            var response = await admin.PostFormAsync($"/invoices/{invoice.Id}/paid", $"/invoices/{invoice.Id}", new Dictionary<string, string>
            {
                ["PaidDate"] = PeopleHelpers.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["AmountReceivedUsd"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["RowVersion"] = rowVersion,
            });
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        // Exact: no pill, nothing outstanding for it.
        await PayAsync(first, first.TotalUsd);
        var exact = await admin.GetStringAsync($"/invoices/{first.Id}");
        Assert.DoesNotContain("data-testid=\"payment-difference\"", exact);
        var list = WebUtility.HtmlDecode(await admin.GetStringAsync("/invoices"));
        Assert.Contains($"data-testid=\"total-outstanding\"", list);
        Assert.Contains(DisplayFormat.Usd(second.TotalUsd), Tile(list, "total-outstanding"));

        // Short by $10.00: outstanding = the unpaid second invoice + the shortfall.
        await admin.PostFormAsync($"/invoices/{first.Id}/unpaid", $"/invoices/{first.Id}", new Dictionary<string, string> { ["rowVersion"] = RowVersionOf(await admin.GetStringAsync($"/invoices/{first.Id}")) });
        await PayAsync(first, first.TotalUsd - 10m);
        Assert.Contains("Short by $10.00", WebUtility.HtmlDecode(await admin.GetStringAsync($"/invoices/{first.Id}")));
        list = WebUtility.HtmlDecode(await admin.GetStringAsync("/invoices"));
        Assert.Contains("Short by $10.00", list);
        Assert.Contains(DisplayFormat.Usd(second.TotalUsd + 10m), Tile(list, "total-outstanding"));
        var dashboard = WebUtility.HtmlDecode(await admin.GetStringAsync("/"));
        Assert.Contains(DisplayFormat.Usd(second.TotalUsd + 10m), Tile(dashboard, "invoices-tile"));
        Assert.Contains("1 paid short", Tile(dashboard, "invoices-tile"));

        // Over by $12.50 on the second: outstanding is only the shortfall; the excess is separate.
        await PayAsync(second, second.TotalUsd + 12.5m);
        list = WebUtility.HtmlDecode(await admin.GetStringAsync("/invoices"));
        Assert.Contains("Over by $12.50", list);
        Assert.Contains("$10.00", Tile(list, "total-outstanding"));
        Assert.Contains("$12.50", Tile(list, "total-excess"));
        Assert.Contains("received in excess $12.50", WebUtility.HtmlDecode(await admin.GetStringAsync("/")));
    }

    private static string Tile(string html, string testId)
    {
        var start = html.IndexOf($"data-testid=\"{testId}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No {testId} tile.");
        return html.Substring(Math.Max(0, start - 200), Math.Min(900, html.Length - Math.Max(0, start - 200)));
    }

    private static string RowVersionOf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"(?:RowVersion|rowVersion)\" value=\"([^\"]*)\"").Groups[1].Value);

    // ===================== Excel exports =====================

    [Fact]
    public async Task Every_export_downloads_with_safe_headers_and_opens()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin);
        var invoice = await InvoiceForAsync(s.Run1);
        var today = PeopleHelpers.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var excel = new (string Url, string FileName)[]
        {
            ("/people/export?status=All", $"people-{today}.xlsx"),
            ("/salaries/export", $"salaries-{today}.xlsx"),
            ("/absences/export?period=2026-10-01&status=All", "absences-2026-10-01-2026-10-15.xlsx"),
            ("/absences/export?from=2026-01-01&to=2026-12-31&status=All", "absences-2026-01-01-2026-12-31.xlsx"),
            ("/exchange-rates/export", $"exchange-rates-{today}.xlsx"),
            ($"/payroll/{s.Run1}/export", "payroll-2026-10-01.xlsx"),
            ($"/payroll/{s.Run1}/register/export", "register-2026-10-01.xlsx"),
            ($"/invoices/{invoice.Id}/export", $"{invoice.Number}.xlsx"),
            ("/owner-income/export?view=Month&at=2026-10-01", "owner-income-month-2026-10-01.xlsx"),
            ("/reports/payroll-history/export", $"payroll-history-{today}.xlsx"),
            ("/reports/salary-changes/export?from=2026-01-01&to=2026-12-31", "salary-changes-2026-01-01-2026-12-31.xlsx"),
            ("/reports/absences/export?from=2026-01-01&to=2026-12-31", "absence-summary-2026-01-01-2026-12-31.xlsx"),
            ("/reports/headcount/export", $"headcount-{today}.xlsx"),
        };
        foreach (var (url, fileName) in excel)
        {
            var (bytes, name) = await DownloadAsync(admin, url, Xlsx);
            Assert.Equal(fileName, name);
            var sheets = Open(bytes);
            Assert.Equal("About", sheets[^1].Name);
            Assert.All(sheets.SkipLast(1), sheet => Assert.NotEmpty(sheet.Headers));
        }

        var people = (await LinesAsync(s.Run1)).Count;
        var pdfs = new (string Url, string FileName, int Pages)[]
        {
            ($"/invoices/{invoice.Id}/pdf", $"{invoice.Number}.pdf", 1),
            ($"/payroll/{s.Run1}/payslips/pdf", "payslips-2026-10-01.pdf", people), // one page per person
            ($"/payroll/{s.Run1}/register/pdf", "register-2026-10-01.pdf", 1),
        };
        foreach (var (url, fileName, expectedPages) in pdfs)
        {
            var (bytes, name) = await DownloadAsync(admin, url, Pdf);
            Assert.Equal(fileName, name);
            var (text, pages) = PdfText(bytes);
            Assert.NotEmpty(text);
            Assert.Equal(expectedPages, pages);
        }

        var line = (await LinesAsync(s.Run1)).Single(l => l.PersonId == s.Ayesha);
        var (payslip, payslipName) = await DownloadAsync(admin, $"/payroll/{s.Run1}/lines/{line.Id}/payslip/pdf", Pdf);
        Assert.Equal($"payslip-{line.PersonCode}-2026-10-01.pdf", payslipName);
        Assert.Equal(1, PdfText(payslip).Pages);
    }

    [Fact]
    public async Task Row_counts_match_the_pages_with_the_same_filters()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin);
        await using var scope = App.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var peopleTotal = (await services.GetRequiredService<PersonService>().ListAsync(new PeopleQuery(null, PersonType.Employee, PersonStatusFilter.All, PersonSort.Name, false, 1))).TotalCount;
        Assert.Equal(peopleTotal, DataRows(Open((await DownloadAsync(admin, "/people/export?status=All&type=Employee", Xlsx)).Bytes)).Count);
        var searched = (await services.GetRequiredService<PersonService>().ListAsync(new PeopleQuery("Golden", null, PersonStatusFilter.Active, PersonSort.Name, false, 1))).TotalCount;
        Assert.Equal(searched, DataRows(Open((await DownloadAsync(admin, "/people/export?q=Golden", Xlsx)).Bytes)).Count);

        var salaries = await services.GetRequiredService<SalaryOverviewService>().ListAsync(new SalaryQuery(null, SalaryFilter.All, SalarySort.Name, false, 1));
        Assert.Equal(salaries.TotalCount, DataRows(Open((await DownloadAsync(admin, "/salaries/export", Xlsx)).Bytes)).Count);

        var absences = await services.GetRequiredService<AbsenceService>().ListAsync(new AbsenceQuery(Oct(16), null, null, PaidStatusFilter.Unpaid, PersonStatusFilter.All, 1));
        var absenceSheets = Open((await DownloadAsync(admin, "/absences/export?period=2026-10-16&paid=Unpaid&status=All", Xlsx)).Bytes);
        Assert.Equal(absences.Rows.TotalCount, DataRows(absenceSheets).Count);
        Assert.Equal(2, absences.Rows.TotalCount); // Bilal: Oct 20 and Oct 27
        Assert.Equal("By person", absenceSheets[1].Name);

        var lines = await services.GetRequiredService<PayrollService>().LinesAsync(s.Run2);
        Assert.Equal(lines.Count, DataRows(Open((await DownloadAsync(admin, $"/payroll/{s.Run2}/export", Xlsx)).Bytes)).Count);
        var register = await services.GetRequiredService<PayrollService>().RegisterAsync(s.Run2);
        var registerSheets = Open((await DownloadAsync(admin, $"/payroll/{s.Run2}/register/export", Xlsx)).Bytes);
        Assert.Equal(register.Count, DataRows(registerSheets).Count);
        Assert.Equal(register.Sum(r => r.NetPayPkr ?? 0m), (decimal)registerSheets[0].Rows[^1][4].GetDouble());

        var invoice = await InvoiceForAsync(s.Run2);
        var invoiceSheets = Open((await DownloadAsync(admin, $"/invoices/{invoice.Id}/export", Xlsx)).Bytes);
        Assert.Equal(["#", "Name", "Designation", "Days", "Salary (USD)", "Extras (USD)", "Amount (USD)"], invoiceSheets[0].Headers);
        Assert.Equal(invoice.Lines.Count, DataRows(invoiceSheets).Count);
        Assert.Equal(invoice.TotalUsd, (decimal)invoiceSheets[0].Rows[^1][6].GetDouble());
    }

    [Fact]
    public async Task Manager_exports_never_contain_billing_data()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var line = (await LinesAsync(s.Run1)).Single(l => l.PersonId == s.Bilal);

        foreach (var url in new[]
                 {
                     "/people/export?status=All", "/salaries/export", "/absences/export?from=2026-01-01&to=2026-12-31&status=All",
                     $"/payroll/{s.Run1}/export", $"/payroll/{s.Run2}/export", $"/payroll/{s.Run1}/register/export",
                     "/reports/payroll-history/export", "/reports/salary-changes/export?from=2026-01-01&to=2026-12-31",
                     "/reports/absences/export?from=2026-01-01&to=2026-12-31", "/reports/headcount/export",
                 })
        {
            var (text, numbers) = Contents((await DownloadAsync(manager, url, Xlsx)).Bytes);
            AssertNoBillingData(url, text, numbers);
        }

        foreach (var url in new[] { $"/payroll/{s.Run1}/payslips/pdf", $"/payroll/{s.Run2}/payslips/pdf", $"/payroll/{s.Run1}/lines/{line.Id}/payslip/pdf", $"/payroll/{s.Run1}/register/pdf" })
        {
            var (text, _) = PdfText((await DownloadAsync(manager, url, Pdf)).Bytes);
            AssertNoBillingData(url, text, []);
            Assert.DoesNotContain("$500.00", text, StringComparison.Ordinal);
            Assert.DoesNotContain("$175.00", text, StringComparison.Ordinal);
        }

        // The Admin's run export does carry billing (the check above is not vacuous).
        var (adminText, adminNumbers) = Contents((await DownloadAsync(admin, $"/payroll/{s.Run1}/export", Xlsx)).Bytes);
        Assert.Contains("invoice (usd)", adminText);
        Assert.Contains("budget hire", adminText);
        Assert.Contains(500.00m, adminNumbers);
    }

    [Fact]
    public async Task Admin_only_exports_are_403_for_Managers_and_exports_need_a_login()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var invoice = await InvoiceForAsync(s.Run1);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        foreach (var url in new[] { $"/invoices/{invoice.Id}/export", $"/invoices/{invoice.Id}/pdf", "/owner-income/export?view=Month&at=2026-10-01" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(url)).StatusCode);
        }

        var anonymous = App.CreateHttpsClient();
        foreach (var url in new[] { "/people/export", $"/payroll/{s.Run1}/register/export", $"/payroll/{s.Run1}/payslips/pdf", "/reports", "/reports/headcount/export", $"/invoices/{invoice.Id}/pdf" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("login", response.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
        }

        // Exports are GET only: they change nothing, so a POST is not routed.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await admin.PostAsync("/people/export", new FormUrlEncodedContent([]))).StatusCode);
    }

    [Fact]
    public async Task A_formula_name_is_literal_text_and_only_the_register_has_full_IBANs()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);

        var (peopleBytes, _) = await DownloadAsync(admin, "/people/export?status=All", Xlsx);
        using (var workbook = new XLWorkbook(new MemoryStream(peopleBytes)))
        {
            var cell = workbook.Worksheet("People").CellsUsed().Single(c => c.GetString() == Hyperlink);
            Assert.False(cell.HasFormula);
            Assert.Equal(XLDataType.Text, cell.DataType);
            Assert.True(cell.Style.IncludeQuotePrefix);
            Assert.DoesNotContain(workbook.Worksheets.SelectMany(w => w.CellsUsed()), c => c.HasFormula);
        }

        var (peopleText, _) = Contents(peopleBytes);
        Assert.DoesNotContain(AyeshaIban.ToLowerInvariant(), peopleText.Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(AyeshaCnic, peopleText);
        Assert.Contains("6702", peopleText);

        var registerSheets = Open((await DownloadAsync(admin, $"/payroll/{s.Run1}/register/export", Xlsx)).Bytes);
        var ayeshaRow = DataRows(registerSheets).Single(r => r[1].GetString() == "Ayesha Golden");
        Assert.Equal(AyeshaIban, Col(registerSheets[0], ayeshaRow, "IBAN").GetString());
        Assert.Contains(PakistaniIban.Format(AyeshaIban), PdfText((await DownloadAsync(admin, $"/payroll/{s.Run1}/register/pdf", Pdf)).Bytes).Text, StringComparison.Ordinal);

        foreach (var url in new[] { "/salaries/export", "/absences/export?from=2026-01-01&to=2026-12-31&status=All", $"/payroll/{s.Run1}/export" })
        {
            var (text, _) = Contents((await DownloadAsync(admin, url, Xlsx)).Bytes);
            Assert.DoesNotContain(AyeshaIban.ToLowerInvariant(), text);
            Assert.DoesNotContain(AyeshaCnic, text);
        }
    }

    // ===================== PDFs =====================

    [Fact]
    public async Task The_invoice_PDF_has_the_lines_and_total_and_none_of_the_internal_words()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var invoice = await InvoiceForAsync(s.Run1);

        var (bytes, name) = await DownloadAsync(admin, $"/invoices/{invoice.Id}/pdf", Pdf);
        Assert.Equal(invoice.Number + ".pdf", name);
        var (text, pages) = PdfText(bytes);
        Assert.Equal(1, pages);
        // Exact extracted text: hyphen-minus in the number, the grouped IBAN, and the dates as printed.
        var words = text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(invoice.Number, words); // e.g. "TWR-2026-0001", U+002D only
        Assert.Matches("^[A-Z0-9]+-2026-\\d{4}$", invoice.Number);
        Assert.Contains("PK36 SCBL 0000 0011 2345 6702", text, StringComparison.Ordinal);
        Assert.Contains("Period 01–15 Oct 2026", text, StringComparison.Ordinal); // the en dash is the intended range dash
        Assert.Contains($"Issue date {DisplayFormat.Date(invoice.IssueDate)}", text, StringComparison.Ordinal);
        Assert.Contains($"Due date {DisplayFormat.Date(invoice.DueDate)}", text, StringComparison.Ordinal);
        Assert.DoesNotContain('−', text); // no minus signs anywhere
        Assert.Contains("Client Corp", text, StringComparison.Ordinal);
        Assert.Contains("Tower Staffing", text, StringComparison.Ordinal);
        foreach (var line in invoice.Lines)
        {
            Assert.Contains(DisplayFormat.Usd(line.AmountUsd), text, StringComparison.Ordinal);
        }

        foreach (var person in new[] { "Ayesha Golden", "Bilal Golden", "Imran Golden" })
        {
            Assert.Contains(person, text, StringComparison.Ordinal);
        }

        Assert.Contains(DisplayFormat.Usd(invoice.TotalUsd), text, StringComparison.Ordinal);
        foreach (var word in new[] { "commission", "margin", "budget", "earning" })
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Draft_payslip_and_register_PDFs_carry_the_watermark()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var draftLine = (await LinesAsync(s.Run2)).Single(l => l.PersonId == s.Ayesha);
        var finalLine = (await LinesAsync(s.Run1)).Single(l => l.PersonId == s.Ayesha);

        var (draft, draftName) = await DownloadAsync(admin, $"/payroll/{s.Run2}/lines/{draftLine.Id}/payslip/pdf", Pdf);
        Assert.EndsWith("-draft.pdf", draftName, StringComparison.Ordinal);
        var draftText = PdfText(draft).Text;
        Assert.Contains("DRAFT", draftText, StringComparison.Ordinal);
        Assert.Contains("NOT FINAL", draftText, StringComparison.Ordinal);
        Assert.Contains("Ayesha Golden", draftText, StringComparison.Ordinal);
        Assert.Contains(DisplayFormat.Pkr(draftLine.NetPayPkr!.Value), draftText, StringComparison.Ordinal);
        Assert.Contains("NOT FINAL", PdfText((await DownloadAsync(admin, $"/payroll/{s.Run2}/register/pdf", Pdf)).Bytes).Text, StringComparison.Ordinal);
        Assert.Contains("NOT FINAL", PdfText((await DownloadAsync(admin, $"/payroll/{s.Run2}/payslips/pdf", Pdf)).Bytes).Text, StringComparison.Ordinal);

        var finalText = PdfText((await DownloadAsync(admin, $"/payroll/{s.Run1}/lines/{finalLine.Id}/payslip/pdf", Pdf)).Bytes).Text;
        Assert.DoesNotContain("NOT FINAL", finalText, StringComparison.Ordinal);
        Assert.Contains("Rs 42,000", finalText, StringComparison.Ordinal); // G1 net pay
    }

    // ===================== Reports =====================

    [Fact]
    public async Task Payroll_history_totals_equal_the_frozen_lines()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin);
        var lines1 = await LinesAsync(s.Run1);
        var lines2 = await LinesAsync(s.Run2);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/reports/payroll-history"));
        Assert.Contains(DisplayFormat.Pkr(lines1.Sum(l => l.NetPayPkr ?? 0m) + lines2.Sum(l => l.NetPayPkr ?? 0m)), page);
        Assert.Contains("data-testid=\"netPayChart\"", page);
        Assert.Contains("data-testid=\"earningChart\"", page);

        var sheets = Open((await DownloadAsync(admin, "/reports/payroll-history/export", Xlsx)).Bytes);
        var rows = DataRows(sheets);
        Assert.Equal(2, rows.Count);
        var october16 = rows[0]; // newest first
        Assert.Equal(lines2.Sum(l => l.NetPayPkr ?? 0m), (decimal)Col(sheets[0], october16, "Net pay (PKR)").GetDouble());
        Assert.Equal(lines2.Sum(l => l.InvoiceUsd ?? 0m), (decimal)Col(sheets[0], october16, "Invoice (USD)").GetDouble());
        Assert.Equal(lines2.Sum(l => l.OwnerEarningUsd ?? 0m), (decimal)Col(sheets[0], october16, "Owner earning (USD)").GetDouble());
        Assert.Equal(lines2.Count, (int)Col(sheets[0], october16, "People").GetDouble());

        // G6 + a full CompanyRecommended (Ayesha) + G5 is $747.73; the run has more people, so check the Owner line is whole.
        Assert.Contains(lines2, l => l.PersonId == s.Imran && l.OwnerEarningUsd == 600.00m);

        // Managers see people and net pay only.
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var managerPage = WebUtility.HtmlDecode(await manager.GetStringAsync("/reports/payroll-history"));
        Assert.DoesNotContain("earning", managerPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoice", managerPage, StringComparison.Ordinal);
        Assert.Equal(["Period start", "Period end", "People", "Net pay (PKR)", "Finalized"], Open((await DownloadAsync(manager, "/reports/payroll-history/export", Xlsx)).Bytes)[0].Headers);
    }

    [Fact]
    public async Task Absence_summary_and_salary_changes_match_the_data()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        await PayAsync(s.Nadia, new AdminPayInput(Oct(16), 321m, 25m, null, null, null));

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync("/reports/absences?from=2026-10-01&to=2026-10-31"));
        Assert.Contains("data-testid=\"absence-summary-table\"", page);
        var sheets = Open((await DownloadAsync(admin, "/reports/absences/export?from=2026-10-01&to=2026-10-31", Xlsx)).Bytes);
        var rows = DataRows(sheets).ToDictionary(r => r[1].GetString());
        Assert.Equal(2, rows.Count);
        Assert.Equal((1.5, 1.0, 0.5), (Col(sheets[0], rows["Kamran Golden"], "Absent days").GetDouble(), Col(sheets[0], rows["Kamran Golden"], "Paid leave used").GetDouble(), Col(sheets[0], rows["Kamran Golden"], "Unpaid days").GetDouble()));
        Assert.Equal((3.0, 1.0, 2.0), (Col(sheets[0], rows["Bilal Golden"], "Absent days").GetDouble(), Col(sheets[0], rows["Bilal Golden"], "Paid leave used").GetDouble(), Col(sheets[0], rows["Bilal Golden"], "Unpaid days").GetDouble()));
        Assert.Equal(3.0, Col(sheets[0], rows["Bilal Golden"], "Oct 2026").GetDouble());

        var tooLong = WebUtility.HtmlDecode(await admin.GetStringAsync("/reports/absences?from=2026-01-01&to=2027-01-01"));
        Assert.Contains(HR.Domain.Reports.ReportRange.TooLongMessage, tooLong);
        Assert.Equal(HttpStatusCode.Redirect, (await admin.GetAsync("/absences/export?from=2026-01-01&to=2027-06-01")).StatusCode);

        var changes = Open((await DownloadAsync(admin, "/reports/salary-changes/export?from=2026-10-01&to=2026-10-31", Xlsx)).Bytes);
        var change = Assert.Single(DataRows(changes));
        Assert.Equal("Nadia Golden", Col(changes[0], change, "Name").GetString());
        Assert.Equal(300.0, Col(changes[0], change, "Old pay (USD)").GetDouble());
        Assert.Equal(321.0, Col(changes[0], change, "New pay (USD)").GetDouble());
        Assert.Equal(7.0, Col(changes[0], change, "Change %").GetDouble());
        Assert.Equal(321.0, Col(changes[0], change, "New billed monthly (USD)").GetDouble());
        Assert.Contains("+7.00%", WebUtility.HtmlDecode(await admin.GetStringAsync("/reports/salary-changes?from=2026-10-01&to=2026-10-31")));
    }

    [Fact]
    public async Task Headcount_matches_the_seeded_joiners_and_leavers()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        await SeedAsync(admin, finalizeSecond: false);
        var app = AppAt(Oct(31));
        var client = await SignInAsync(app, AppRoles.Manager);

        var page = await client.GetStringAsync("/reports/headcount");
        Assert.Contains("data-testid=\"headcountChart\"", page);
        var sheets = Open((await DownloadAsync(client, "/reports/headcount/export", Xlsx)).Bytes);
        var rows = DataRows(sheets);
        Assert.Equal(12, rows.Count);
        var october = rows[^1];
        Assert.Equal(new DateTime(2026, 10, 1), Col(sheets[0], october, "Month").GetDateTime());
        // 7 people seeded: Rizwan left on Oct 21, Nadia joined on Oct 8.
        Assert.Equal((6.0, 6.0, 0.0, 1.0, 1.0), (Col(sheets[0], october, "Active").GetDouble(), Col(sheets[0], october, "Employees").GetDouble(),
            Col(sheets[0], october, "Internees").GetDouble(), Col(sheets[0], october, "Joiners").GetDouble(), Col(sheets[0], october, "Leavers").GetDouble()));
        var september = rows[^2];
        Assert.Equal((6.0, 0.0, 0.0), (Col(sheets[0], september, "Active").GetDouble(), Col(sheets[0], september, "Joiners").GetDouble(), Col(sheets[0], september, "Leavers").GetDouble()));
    }

    // ===================== Payslips tab, dashboard, pages =====================

    [Fact]
    public async Task The_Payslips_tab_lists_only_finalized_lines_without_billing()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);
        var finalLine = (await LinesAsync(s.Run1)).Single(l => l.PersonId == s.Ayesha);
        await admin.GetStringAsync("/"); // shows (and clears) the "Draft payroll … generated" toast

        foreach (var client in new[] { admin, manager })
        {
            var page = WebUtility.HtmlDecode(await client.GetStringAsync($"/people/{s.Ayesha}?tab=payslips"));
            Assert.Contains("data-testid=\"payslips-tab\"", page);
            var html = page[page.IndexOf("data-testid=\"payslips-tab\"", StringComparison.Ordinal)..]; // the tab only (not toasts)
            Assert.Single(Regex.Matches(html, "data-testid=\"payslip-row\""));
            Assert.Contains("01–15 Oct 2026", html);
            Assert.DoesNotContain("16–31 Oct 2026", html); // the draft
            Assert.Contains("Rs 42,000", html);
            Assert.Contains($"/payroll/{s.Run1}/lines/{finalLine.Id}/payslip/pdf", html);
            Assert.DoesNotContain("$175.00", html);
            Assert.DoesNotContain("commission", html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("data-testid=\"payslips-empty\"", await admin.GetStringAsync($"/people/{await App.CreatePersonAsync("New Starter", joined: Oct(16))}?tab=payslips"));
    }

    [Fact]
    public async Task The_dashboard_trend_card_shows_net_pay_and_earnings_for_Admins_only()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin);
        var lines2 = await LinesAsync(s.Run2);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        var adminHtml = WebUtility.HtmlDecode(await admin.GetStringAsync("/"));
        Assert.Contains("data-testid=\"trend-card\"", adminHtml);
        Assert.Contains("data-testid=\"sparkline-netpay\"", adminHtml);
        Assert.Contains("data-testid=\"sparkline-earning\"", adminHtml);
        Assert.Contains(DisplayFormat.Pkr(lines2.Sum(l => l.NetPayPkr ?? 0m)), adminHtml);
        Assert.Contains(DisplayFormat.Usd(lines2.Sum(l => l.OwnerEarningUsd ?? 0m)), adminHtml);

        var managerHtml = WebUtility.HtmlDecode(await manager.GetStringAsync("/"));
        Assert.Contains("data-testid=\"sparkline-netpay\"", managerHtml);
        Assert.DoesNotContain("sparkline-earning", managerHtml);
        Assert.DoesNotContain("earning", managerHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_are_live_for_both_roles_and_new_pages_have_no_inline_script_or_style()
    {
        var (admin, _) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var (manager, _) = await App.SignInAsAsync(AppRoles.Manager);

        foreach (var client in new[] { admin, manager })
        {
            var home = await client.GetStringAsync("/");
            Assert.Contains("href=\"/reports\"", home);
            Assert.DoesNotContain("Reports (coming soon)", home);

            foreach (var url in new[]
                     {
                         "/", "/reports", "/reports/payroll-history", "/reports/salary-changes", "/reports/absences", "/reports/headcount",
                         $"/people/{s.Ayesha}?tab=payslips", "/people", "/salaries", "/absences", "/exchange-rates", $"/payroll/{s.Run1}", $"/payroll/{s.Run1}/register",
                     })
            {
                var response = await client.GetAsync(url);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var html = await response.Content.ReadAsStringAsync();
                Assert.DoesNotMatch(new Regex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);
                Assert.DoesNotMatch(new Regex("\\sstyle\\s*=", RegexOptions.IgnoreCase), html);
                Assert.DoesNotMatch(new Regex("\\son[a-z]+\\s*=\\s*\"", RegexOptions.IgnoreCase), html);
                Assert.Single(Regex.Matches(html, "<h1[\\s>]"));
            }
        }

        Assert.Equal(4, Regex.Matches(await manager.GetStringAsync("/reports"), "data-testid=\"report-card\"").Count);
    }

    [Fact]
    public async Task Exports_are_audited_without_row_contents()
    {
        var (admin, adminUser) = await App.SignInAsAsync(AppRoles.Admin);
        var s = await SeedAsync(admin, finalizeSecond: false);
        var invoice = await InvoiceForAsync(s.Run1);
        var before = App.Logs.Entries.Count;

        await DownloadAsync(admin, "/people/export?status=All&q=Golden", Xlsx);
        await DownloadAsync(admin, $"/payroll/{s.Run1}/register/export", Xlsx);
        await DownloadAsync(admin, $"/invoices/{invoice.Id}/pdf", Pdf);

        var events = App.Logs.Entries.Skip(before).Where(e => e.EventId.Id == 1700).ToList();
        Assert.Equal(3, events.Count);
        Assert.Contains($"ActorId={adminUser.Id}", events[0].Values);
        Assert.Contains("Report=People", events[0].Values);
        Assert.Contains("Format=xlsx", events[0].Values);
        Assert.Contains("Search=(given)", events[0].AllText, StringComparison.Ordinal);
        Assert.Contains("Report=Payroll register", events[1].Values);
        Assert.Contains($"RowCount={(await LinesAsync(s.Run1)).Count}", events[1].Values);
        Assert.Contains("Format=pdf", events[2].Values);
        foreach (var e in events)
        {
            foreach (var secret in new[] { "Golden", AyeshaIban, AyeshaCnic, "42000", "42,000", "Client Corp" })
            {
                Assert.DoesNotContain(secret, e.AllText, StringComparison.Ordinal);
            }
        }
    }
}
