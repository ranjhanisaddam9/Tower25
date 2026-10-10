using ClosedXML.Excel;
using HR.Domain.Exports;
using HR.Domain.Invoices;
using HR.Domain.Pay;
using HR.Domain.People;
using HR.Domain.Reports;
using HR.Infrastructure.Exports;
using HR.Infrastructure.Invoices;
using HR.Infrastructure.People;
using HR.Web.Exports;

namespace HR.Tests.Unit;

/// <summary>M9: spreadsheet injection, typed cells, file names, masking, payment differences, headcount and salary changes.</summary>
public class SpreadsheetBuilderTests
{
    private static ExportContext Context => new("Test", "Tester", new DateTimeOffset(2026, 10, 10, 5, 0, 0, TimeSpan.Zero), [new("Status", "Active")]);

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")")]
    [InlineData("+92300")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tTabbed")]
    [InlineData("\rCarriage")]
    public void Formula_like_text_is_stored_as_quote_prefixed_text_never_as_a_formula(string text)
    {
        Assert.True(SpreadsheetText.IsFormulaLike(text));
        var bytes = new SpreadsheetBuilder(Context).AddSheet("Sheet", [new ExportColumn<string>("Name", CellKind.Text, s => s)], [text]).Build();

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var cell = workbook.Worksheet("Sheet").Cell(2, 1);
        Assert.False(cell.HasFormula);
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal(text.Replace('\r', '\n'), cell.GetString()); // XML stores a lone CR as a line feed
        Assert.True(cell.Style.IncludeQuotePrefix);
    }

    [Theory]
    [InlineData("Ayesha Khan")]
    [InlineData("O'Neil = friend")]
    [InlineData("")]
    public void Ordinary_text_is_plain_text(string text)
    {
        Assert.False(SpreadsheetText.IsFormulaLike(text));
        using var workbook = new XLWorkbook();
        var cell = workbook.AddWorksheet("S").Cell(1, 1);
        SpreadsheetBuilder.WriteText(cell, text);
        Assert.False(cell.HasFormula);
        Assert.False(cell.Style.IncludeQuotePrefix);
    }

    [Fact]
    public void Money_dates_and_days_are_typed_cells_with_formats()
    {
        var bytes = new SpreadsheetBuilder(Context)
            .AddSheet("Money",
            [
                new ExportColumn<int>("USD", CellKind.Usd, _ => 1234.5m),
                new ExportColumn<int>("PKR", CellKind.Pkr, _ => 196000m),
                new ExportColumn<int>("Date", CellKind.Date, _ => new DateOnly(2026, 10, 16)),
                new ExportColumn<int>("Days", CellKind.Days, _ => 10.5m),
                new ExportColumn<int>("Rate", CellKind.Rate, _ => 280.1234m),
                new ExportColumn<int>("Change", CellKind.Percent, _ => 7.14m),
                new ExportColumn<int>("Empty", CellKind.Usd, _ => null),
            ], [1], totals: ["Total", 1234.5m])
            .Build();

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        var sheet = workbook.Worksheet("Money");
        Assert.Equal(XLDataType.Number, sheet.Cell(2, 1).DataType);
        Assert.Equal(1234.5, sheet.Cell(2, 1).GetDouble());
        Assert.Equal(SpreadsheetBuilder.UsdFormat, sheet.Cell(2, 1).Style.NumberFormat.Format);
        Assert.Equal("$#,##0.00", SpreadsheetBuilder.UsdFormat);
        Assert.Equal(SpreadsheetBuilder.PkrFormat, sheet.Cell(2, 2).Style.NumberFormat.Format);
        Assert.Equal("\"Rs \"#,##0", SpreadsheetBuilder.PkrFormat);
        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 3).DataType);
        Assert.Equal(new DateTime(2026, 10, 16), sheet.Cell(2, 3).GetDateTime());
        Assert.Equal(SpreadsheetBuilder.DateFormat, sheet.Cell(2, 3).Style.NumberFormat.Format);
        Assert.Equal(10.5, sheet.Cell(2, 4).GetDouble());
        Assert.Equal(SpreadsheetBuilder.RateFormat, sheet.Cell(2, 5).Style.NumberFormat.Format);
        Assert.Equal(7.14, sheet.Cell(2, 6).GetDouble());
        Assert.True(sheet.Cell(2, 7).IsEmpty());

        // Bold frozen header with an auto-filter; bold totals row.
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal("Total", sheet.Cell(3, 1).GetString());
        Assert.True(sheet.Cell(3, 2).Style.Font.Bold);
    }

    [Fact]
    public void Every_workbook_ends_with_an_About_sheet()
    {
        var builder = new SpreadsheetBuilder(Context).AddSheet("People", [new ExportColumn<string>("Name", CellKind.Text, s => s)], ["A", "B", "C"]);
        var bytes = builder.Build();
        Assert.Equal(3, builder.RowCount);

        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["People", "About"], workbook.Worksheets.Select(w => w.Name));
        var about = workbook.Worksheet("About");
        var values = Enumerable.Range(2, 5).ToDictionary(r => about.Cell(r, 1).GetString(), r => about.Cell(r, 2));
        Assert.Equal("Test", values["Report"].GetString());
        Assert.Equal(new DateTime(2026, 10, 10, 10, 0, 0), values["Generated at (Asia/Karachi)"].GetDateTime()); // 05:00 UTC = 10:00 PKT
        Assert.Equal("Tester", values["Generated by"].GetString());
        Assert.Equal(3, values["Rows"].GetDouble());
        Assert.Equal("Active", values["Filter: Status"].GetString());
    }

    [Theory]
    [InlineData("Payroll: Oct/16 [draft]*?", "Payroll- Oct-16 -draft---")]
    [InlineData("A very long sheet name that goes on and on", "A very long sheet name that goe")]
    public void Sheet_names_are_valid_for_Excel(string name, string expected) => Assert.Equal(expected, SpreadsheetBuilder.SheetName(name));
}

public class ExportFileNameTests
{
    [Theory]
    [InlineData("payroll-2026-10-16.xlsx", "xlsx", "payroll", "2026-10-16")]
    [InlineData("T25-2026-0003.pdf", "pdf", "T25-2026-0003")]
    [InlineData("payslip-EMP-0007-2026-10-01-draft.pdf", "pdf", "payslip", "EMP-0007", "2026-10-01", "draft")]
    [InlineData("people.xlsx", "xlsx", "people", null, "")]
    [InlineData("a-b-c.xlsx", "xlsx", "a\"b/c")]
    [InlineData("A-ysha.xlsx", "xlsx", "Aïysha")]
    [InlineData("export.xlsx", "xlsx", "\"\"//")]
    [InlineData("x.xlsx", ".x/l\"s\\x", "x")]
    public void Builds_safe_ascii_names(string expected, string extension, params string?[] parts) =>
        Assert.Equal(expected, ExportFileName.Build(extension, parts));

    [Fact]
    public void Long_names_are_cut()
    {
        var name = ExportFileName.Build("pdf", new string('a', 200));
        Assert.Equal(ExportFileName.MaxStemLength + ".pdf".Length, name.Length);
        Assert.All(name, c => Assert.True(char.IsAscii(c)));
    }
}

public class ExportMaskingTests
{
    [Fact]
    public void People_export_shows_masked_identifiers_only()
    {
        var cnic = "12345-1234567-1";
        var iban = "PK36SCBL0000001123456702";
        var row = new PersonExportRow(1, "EMP-0001", "Ayesha", PersonType.Employee, "Engineer", null, "+923001234567",
            Masking.Cnic(cnic), "Meezan", Masking.Iban(iban), new DateOnly(2025, 1, 6), null, true);
        var file = ExcelExports.People(new ExportContext("People", "T", DateTimeOffset.UtcNow, []), [row], new DateOnly(2026, 10, 10));

        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var text = string.Join("|", workbook.Worksheet("People").CellsUsed().Select(c => c.GetString()));
        Assert.DoesNotContain(cnic, text);
        Assert.DoesNotContain(iban, text);
        Assert.DoesNotContain("12345-", text);
        Assert.DoesNotContain("PK36", text);
        Assert.Contains("•••••-••••567-1", text);
        Assert.Contains("6702", text);
        Assert.Contains("+923001234567", text); // phones as stored
        Assert.Equal("people-2026-10-10.xlsx", file.FileName);
        Assert.Equal(ExportContentTypes.Xlsx, file.ContentType);
    }
}

public class PaymentDifferenceTests
{
    private static InvoiceRow Row(InvoiceStatus status, decimal total, decimal? received) =>
        new(1, "INV-2026-0001", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 22),
            total, status, false, status == InvoiceStatus.Paid ? new DateOnly(2026, 10, 20) : null, received);

    [Theory]
    [InlineData(1000.00, 990.00, -10.00, 10.00, 0.00)]
    [InlineData(1000.00, 1000.00, 0.00, 0.00, 0.00)]
    [InlineData(1000.00, 1012.50, 12.50, 0.00, 12.50)]
    public void Short_exact_and_over_payment(decimal total, decimal received, decimal difference, decimal outstanding, decimal excess)
    {
        Assert.Equal(difference, InvoiceMath.PaymentDifference(InvoiceStatus.Paid, total, received));
        Assert.Equal(outstanding, InvoiceMath.OutstandingUsd(InvoiceStatus.Paid, total, received));
        Assert.Equal(excess, InvoiceMath.ExcessUsd(InvoiceStatus.Paid, total, received));
    }

    [Fact]
    public void Unpaid_and_void_invoices_have_no_difference()
    {
        Assert.Equal(0m, InvoiceMath.PaymentDifference(InvoiceStatus.Issued, 500m, null));
        Assert.Equal(500m, InvoiceMath.OutstandingUsd(InvoiceStatus.Issued, 500m, null));
        Assert.Equal(0m, InvoiceMath.OutstandingUsd(InvoiceStatus.Void, 500m, null));
        Assert.Equal(0m, InvoiceMath.ExcessUsd(InvoiceStatus.Void, 500m, null));
    }

    [Fact]
    public void Outstanding_is_unpaid_totals_plus_shortfalls_and_excess_is_separate()
    {
        var totals = InvoiceService.Totals(
        [
            Row(InvoiceStatus.Issued, 300m, null),
            Row(InvoiceStatus.Paid, 1000m, 990m),
            Row(InvoiceStatus.Paid, 200m, 200m),
            Row(InvoiceStatus.Paid, 400m, 450m),
            Row(InvoiceStatus.Void, 999m, null),
        ]);
        Assert.Equal(1900m, totals.InvoicedUsd);
        Assert.Equal(1640m, totals.ReceivedUsd);
        Assert.Equal(310m, totals.OutstandingUsd);
        Assert.Equal(50m, totals.ReceivedInExcessUsd);
        Assert.Equal(1, totals.ShortPaidCount);
    }

    [Fact]
    public void Pills_read_short_by_and_over_by()
    {
        Assert.Equal("Short by $10.00", HR.Web.ViewModels.InvoiceDisplay.Difference(InvoiceStatus.Paid, 1000m, 990m)?.Text);
        Assert.Equal("Over by $12.50", HR.Web.ViewModels.InvoiceDisplay.Difference(InvoiceStatus.Paid, 1000m, 1012.5m)?.Text);
        Assert.Null(HR.Web.ViewModels.InvoiceDisplay.Difference(InvoiceStatus.Paid, 1000m, 1000m));
        Assert.Null(HR.Web.ViewModels.InvoiceDisplay.Difference(InvoiceStatus.Issued, 1000m, null));
    }
}

public class ReportRuleTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);

    [Fact]
    public void Headcount_counts_month_end_actives_joiners_and_leavers()
    {
        var people = new[]
        {
            new HeadcountPerson(PersonType.Employee, [new EmploymentSpan(new DateOnly(2025, 1, 6), null)]),
            new HeadcountPerson(PersonType.Employee, [new EmploymentSpan(new DateOnly(2026, 10, 8), null)]),
            new HeadcountPerson(PersonType.Employee, [new EmploymentSpan(new DateOnly(2025, 1, 6), new DateOnly(2026, 10, 21))]),
            new HeadcountPerson(PersonType.Internee, [new EmploymentSpan(new DateOnly(2026, 3, 2), new DateOnly(2026, 5, 29)), new EmploymentSpan(new DateOnly(2026, 8, 3), null)]),
        };
        var months = HeadcountCalculator.LastMonths(people, Today);

        Assert.Equal(12, months.Count);
        Assert.Equal(new DateOnly(2025, 11, 1), months[0].Month);
        var october = months[^1];
        Assert.Equal((2, 1, 1, 1), (october.ActiveEmployees, october.ActiveInternees, october.Joiners, october.Leavers));
        var may = months.Single(m => m.Month.Month == 5);
        Assert.Equal((2, 0, 0, 1), (may.ActiveEmployees, may.ActiveInternees, may.Joiners, may.Leavers));
        Assert.Equal(1, months.Single(m => m.Month.Month == 8).Joiners); // a rejoin is a joiner again
    }

    [Fact]
    public void Headcount_in_the_current_month_counts_up_to_today_only()
    {
        var people = new[] { new HeadcountPerson(PersonType.Employee, [new EmploymentSpan(new DateOnly(2025, 1, 6), new DateOnly(2026, 10, 21))]) };
        var october = HeadcountCalculator.LastMonths(people, new DateOnly(2026, 10, 10))[^1];
        Assert.Equal(new DateOnly(2026, 10, 10), october.AsOf);
        Assert.Equal((1, 0), (october.Active, october.Leavers)); // leaving later this month: not a leaver yet
    }

    [Fact]
    public void Salary_changes_find_increments_and_decrements_in_the_range()
    {
        var history = new[]
        {
            new RatePoint(new DateOnly(2026, 1, 1), 300m, PayCurrency.USD, 300m, 25m),
            new RatePoint(new DateOnly(2026, 4, 1), 321m, PayCurrency.USD, 321m, 25m),
            new RatePoint(new DateOnly(2026, 6, 16), 321m, PayCurrency.USD, 321m, 30m),
            new RatePoint(new DateOnly(2026, 9, 1), 300m, PayCurrency.USD, 300m, 30m),
            new RatePoint(new DateOnly(2026, 10, 1), 90_000m, PayCurrency.PKR, 300m, 30m),
        };
        var changes = SalaryChanges.Between(history, new DateOnly(2026, 2, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(4, changes.Count);
        Assert.Equal(7.00m, changes[0].PayChangePercent);
        Assert.Equal((false, true), (changes[1].PayChanged, changes[1].BillingChanged)); // commission only (Admin)
        Assert.Equal(-6.54m, changes[2].PayChangePercent);
        Assert.Null(changes[3].PayChangePercent); // currency changed

        // Managers' points carry no billing, so a billing-only change never appears for them.
        var managerView = SalaryChanges.Between(history.Select(r => r with { BilledMonthlyUsd = null, CommissionPerPeriodUsd = null }), new DateOnly(2026, 2, 1), new DateOnly(2026, 12, 31));
        Assert.Equal(3, managerView.Count);
        Assert.All(managerView, c => Assert.False(c.BillingChanged));
    }

    [Theory]
    [InlineData("2026-01-01", "2026-12-31", null)]
    [InlineData("2026-01-15", "2027-01-14", null)]
    [InlineData("2026-01-01", "2027-01-01", ReportRange.TooLongMessage)]
    [InlineData("2026-05-01", "2026-04-30", ReportRange.OrderMessage)]
    public void Ranges_are_at_most_one_year(string from, string to, string? expected) =>
        Assert.Equal(expected, ReportRange.Validate(DateOnly.Parse(from, CultureInfo.InvariantCulture), DateOnly.Parse(to, CultureInfo.InvariantCulture)));

    [Fact]
    public void Range_months_cover_every_month_touched()
    {
        var months = ReportRange.Months(new DateOnly(2026, 1, 15), new DateOnly(2026, 3, 2));
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)], months);
    }
}
