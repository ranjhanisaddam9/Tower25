using System.Globalization;
using HR.Domain.Absences;
using HR.Domain.Exports;
using HR.Domain.Invoices;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Reports;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Exports;
using HR.Infrastructure.Invoices;
using HR.Infrastructure.Pay;
using HR.Infrastructure.Payroll;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Reports;
using HR.Web.Formatting;
using HR.Web.ViewModels;

namespace HR.Web.Exports;

/// <summary>
/// The .xlsx exports (M9 Part C and the report exports). Manager variants take the Manager rows (pay side only) and
/// have no billing columns; Admin variants take the Admin rows and add them. Headers and About-sheet text for Managers
/// never mention billing, commission, margin, earning, budget, invoices or hire sources.
/// </summary>
public static class ExcelExports
{
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Status(bool isActive, DateOnly? leaving, DateOnly today) =>
        !isActive ? "Inactive" : leaving is { } l && l >= today ? "Leaving " + DisplayFormat.Date(l) : "Active";

    private static ExportFile Xlsx(SpreadsheetBuilder builder, params string?[] name) =>
        new(builder.Build(), ExportFileName.Build("xlsx", name), ExportContentTypes.Xlsx, builder.RowCount);

    private static decimal? Usd(decimal? amount, PayCurrency? currency) => currency == PayCurrency.USD ? amount : null;

    private static decimal? Pkr(decimal? amount, PayCurrency? currency) => currency == PayCurrency.PKR ? amount : null;

    // ===================== People =====================

    private static List<ExportColumn<PersonExportRow>> PeopleColumns(DateOnly today) =>
    [
        new("Code", CellKind.Text, p => p.Code),
        new("Name", CellKind.Text, p => p.FullName),
        new("Type", CellKind.Text, p => p.Type.ToString()),
        new("Designation", CellKind.Text, p => p.Designation),
        new("Email", CellKind.Text, p => p.Email),
        new("Phone", CellKind.Text, p => p.Phone),
        new("CNIC (masked)", CellKind.Text, p => p.CnicMasked),
        new("Bank", CellKind.Text, p => p.BankName),
        new("IBAN (masked)", CellKind.Text, p => p.IbanMasked),
        new("Joined", CellKind.Date, p => p.JoiningDate),
        new("Leaving date", CellKind.Date, p => p.LeavingDate),
        new("Status", CellKind.Text, p => Status(p.IsActive, p.LeavingDate, today)),
    ];

    public static ExportFile People(ExportContext context, IReadOnlyList<PersonExportRow> rows, DateOnly today) =>
        Xlsx(new SpreadsheetBuilder(context).AddSheet("People", PeopleColumns(today), rows), "people", Iso(today));

    public static ExportFile PeopleAdmin(ExportContext context, IReadOnlyList<AdminPersonExportRow> rows, DateOnly today)
    {
        var columns = PeopleColumns(today)
            .Select(c => new ExportColumn<AdminPersonExportRow>(c.Header, c.Kind, r => c.Value(r.Person)))
            .Append(new ExportColumn<AdminPersonExportRow>("Hire source", CellKind.Text, r => HireSourceDisplay.Label(r.HireSource)))
            .ToList();
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("People", columns, rows), "people", Iso(today));
    }

    // ===================== Salaries =====================

    private static List<ExportColumn<SalaryRow>> SalaryColumns() =>
    [
        new("Code", CellKind.Text, r => r.Code),
        new("Name", CellKind.Text, r => r.FullName),
        new("Designation", CellKind.Text, r => r.Designation),
        new("Monthly pay (USD)", CellKind.Usd, r => Usd(r.PayMonthlyAmount, r.PayCurrency)),
        new("Monthly pay (PKR)", CellKind.Pkr, r => Pkr(r.PayMonthlyAmount, r.PayCurrency)),
        new("Since", CellKind.Date, r => r.Since),
        new("Starts on", CellKind.Date, r => r.StartsOn),
        new("Last change", CellKind.Date, r => r.LastChangeDate),
        new("Last change %", CellKind.Percent, r => r.LastChangePercent is { } p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null),
        new("Pay set up", CellKind.Text, r => r.HasSetup ? "Yes" : "No"),
    ];

    public static ExportFile Salaries(ExportContext context, IReadOnlyList<SalaryRow> rows, DateOnly today) =>
        Xlsx(new SpreadsheetBuilder(context).AddSheet("Salaries", SalaryColumns(), rows), "salaries", Iso(today));

    public static ExportFile SalariesAdmin(ExportContext context, IReadOnlyList<AdminSalaryRow> rows, AdminSalaryTotals totals, DateOnly today)
    {
        var columns = SalaryColumns()
            .Select(c => new ExportColumn<AdminSalaryRow>(c.Header, c.Kind, r => c.Value(r.Row)))
            .Concat(
            [
                new("Billed monthly (USD)", CellKind.Usd, r => r.BilledMonthlyUsd),
                new("Commission per period (USD)", CellKind.Usd, r => r.CommissionPerPeriodUsd),
                new("Earning per full period (USD)", CellKind.Usd, r => r.EarningPerFullPeriodUsd),
                new("Needs billing review", CellKind.Text, r => r.NeedsReview ? "Yes" : "No"),
            ])
            .ToList();
        var totalsRow = new object?[columns.Count];
        totalsRow[0] = "Total";
        totalsRow[^4] = totals.BilledMonthlyUsd;
        totalsRow[^2] = totals.EarningPerFullPeriodUsd;
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Salaries", columns, rows, totalsRow), "salaries", Iso(today));
    }

    // ===================== Absences =====================

    public static ExportFile Absences(ExportContext context, AbsenceRange range)
    {
        var rows = new List<ExportColumn<AbsenceRow>>
        {
            new("Date", CellKind.Date, r => r.Date),
            new("Code", CellKind.Text, r => r.PersonCode),
            new("Name", CellKind.Text, r => r.PersonName),
            new("Portion", CellKind.Text, r => AbsenceDisplay.PortionLabel(r.Portion)),
            new("Days", CellKind.Days, r => r.Portion.Days()),
            new("Paid leave", CellKind.Days, r => r.PaidDays),
            new("Unpaid", CellKind.Days, r => r.UnpaidDays),
            new("Status", CellKind.Text, r => AbsenceDisplay.PaidLabel(r.PaidDays, r.UnpaidDays)),
            new("Note", CellKind.Text, r => r.Note),
            new("Added by", CellKind.Text, r => r.AddedBy),
        };
        var builder = new SpreadsheetBuilder(context)
            .AddSheet("Absences", rows, range.Rows,
                ["Total", null, null, null, range.Rows.Sum(r => r.Portion.Days()), range.Rows.Sum(r => r.PaidDays), range.Rows.Sum(r => r.UnpaidDays)])
            .AddSheet("By person", AbsenceTotalsColumns(range.Months), range.People);
        return Xlsx(builder, "absences", Iso(range.From), Iso(range.To));
    }

    private static List<ExportColumn<AbsencePersonTotals>> AbsenceTotalsColumns(IReadOnlyList<DateOnly> months) =>
    [
        new("Code", CellKind.Text, t => t.PersonCode),
        new("Name", CellKind.Text, t => t.PersonName),
        new("Absent days", CellKind.Days, t => t.AbsentDays),
        new("Paid leave used", CellKind.Days, t => t.PaidDays),
        new("Unpaid days", CellKind.Days, t => t.UnpaidDays),
        .. months.Select(m => new ExportColumn<AbsencePersonTotals>(m.ToString("MMM yyyy", CultureInfo.InvariantCulture), CellKind.Days, t => t.AbsentByMonth.GetValueOrDefault(m))),
    ];

    /// <summary>The absence summary report: one row per person, absent days by month.</summary>
    public static ExportFile AbsenceSummary(ExportContext context, AbsenceRange range)
    {
        var columns = AbsenceTotalsColumns(range.Months);
        var totals = new List<object?> { "Total", null, range.People.Sum(p => p.AbsentDays), range.People.Sum(p => p.PaidDays), range.People.Sum(p => p.UnpaidDays) };
        totals.AddRange(range.Months.Select(m => (object?)range.People.Sum(p => p.AbsentByMonth.GetValueOrDefault(m))));
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Absence summary", columns, range.People, totals), "absence-summary", Iso(range.From), Iso(range.To));
    }

    // ===================== Exchange rates =====================

    public static ExportFile ExchangeRates(ExportContext context, IReadOnlyList<RateRow> rows, DateOnly today)
    {
        var columns = new List<ExportColumn<RateRow>>
        {
            new("Effective from", CellKind.Date, r => r.EffectiveFrom),
            new("USD to PKR", CellKind.Rate, r => r.UsdToPkr),
            new("Change vs previous %", CellKind.Percent, r => r.PreviousUsdToPkr is { } p && p != 0m ? Math.Round((r.UsdToPkr - p) / p * 100m, 2, MidpointRounding.AwayFromZero) : null),
            new("Note", CellKind.Text, r => r.Note),
            new("Added by", CellKind.Text, r => r.AddedBy),
        };
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Exchange rates", columns, rows), "exchange-rates", Iso(today));
    }

    // ===================== Payroll run lines =====================

    private static List<ExportColumn<PayrollLineRow>> LineColumns() =>
    [
        new("Code", CellKind.Text, l => l.PersonCode),
        new("Name", CellKind.Text, l => l.PersonName),
        new("Designation", CellKind.Text, l => l.Designation),
        new("Type", CellKind.Text, l => l.PersonType.ToString()),
        new("Working days", CellKind.Integer, l => l.WorkingDays),
        new("Employed days", CellKind.Integer, l => l.EmployedWorkingDays),
        new("Unpaid days", CellKind.Days, l => l.UnpaidDays),
        new("Extra days", CellKind.Days, l => l.ExtraDays),
        new("Payable days", CellKind.Days, l => l.PayableDays),
        new("Monthly pay (USD)", CellKind.Usd, l => Usd(l.PayMonthlyAmount, l.PayCurrency)),
        new("Monthly pay (PKR)", CellKind.Pkr, l => Pkr(l.PayMonthlyAmount, l.PayCurrency)),
        new("Pay (PKR)", CellKind.Pkr, l => l.PayPkr),
        new("Pay (USD)", CellKind.Usd, l => l.PayUsd),
        new("Adjustments (PKR)", CellKind.Pkr, l => l.AdjustmentsPkr),
        new("Net pay (PKR)", CellKind.Pkr, l => l.NetPayPkr),
        new("Net pay (USD)", CellKind.Usd, l => l.NetPayUsd),
        new("Note", CellKind.Text, l => l.IsOrphaned ? "No longer employed in this period" : null),
    ];

    private static object?[] LineTotals(IReadOnlyList<PayrollLineRow> lines, int columnCount)
    {
        var active = lines.Where(l => !l.IsOrphaned).ToList();
        var totals = new object?[columnCount];
        totals[0] = "Total";
        totals[11] = active.Sum(l => l.PayPkr ?? 0m);
        totals[12] = active.Sum(l => l.PayUsd ?? 0m);
        totals[13] = active.Sum(l => l.AdjustmentsPkr ?? 0m);
        totals[14] = active.Sum(l => l.NetPayPkr ?? 0m);
        totals[15] = active.Sum(l => l.NetPayUsd ?? 0m);
        return totals;
    }

    private static string RunStatus(PayrollRunHeader run) => run.IsDraft ? "Draft – not final" : "Finalized";

    public static IReadOnlyList<ExportFilter> RunFilters(PayrollRunHeader run) =>
    [
        new("Period", DisplayFormat.DateRange(run.Period.Start, run.Period.End)),
        new("Status", RunStatus(run)),
        new("Exchange rate", run.ExchangeRate is { } r ? "1 USD = " + PayrollDisplay.Rate(r) : "not set"),
    ];

    /// <summary>A line's problem as a Manager may read it.</summary>
    private static string? Issue(PayrollLineRow l, bool isAdmin) => l.Issue is { } issue ? PayrollDisplay.IssueText(issue, isAdmin) : null;

    public static ExportFile RunLines(ExportContext context, PayrollRunHeader run, IReadOnlyList<PayrollLineRow> lines)
    {
        var columns = LineColumns().Append(new ExportColumn<PayrollLineRow>("Problem", CellKind.Text, l => Issue(l, false))).ToList();
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Payroll", columns, lines, LineTotals(lines, columns.Count)), "payroll", Iso(run.Period.Start), run.IsDraft ? "draft" : null);
    }

    public static ExportFile RunLinesAdmin(ExportContext context, PayrollRunHeader run, IReadOnlyList<PayrollLineRow> lines, IReadOnlyDictionary<int, PayrollLineBilling> billing)
    {
        PayrollLineBilling? B(PayrollLineRow l) => billing.GetValueOrDefault(l.Id);
        var shared = LineColumns().Select(c => new ExportColumn<PayrollLineRow>(c.Header, c.Kind, c.Value));
        var columns = shared
            .Concat(
            [
                new("Problem", CellKind.Text, l => Issue(l, true)),
                new("Hire source", CellKind.Text, l => B(l) is { } b ? HireSourceDisplay.Label(b.HireSource) : null),
                new("Billed (USD)", CellKind.Usd, l => B(l)?.BilledUsd),
                new("Adjustments (USD)", CellKind.Usd, l => B(l)?.AdjustmentsUsd),
                new("Invoice (USD)", CellKind.Usd, l => B(l)?.InvoiceUsd),
                new("Owner earning (USD)", CellKind.Usd, l => B(l)?.OwnerEarningUsd),
                new("Owner earning (PKR)", CellKind.Pkr, l => B(l)?.OwnerEarningPkr),
            ])
            .ToList();
        var totals = LineTotals(lines, columns.Count);
        var active = lines.Where(l => !l.IsOrphaned).Select(B).Where(b => b is not null).ToList();
        totals[^5] = active.Sum(b => b!.BilledUsd ?? 0m);
        totals[^4] = active.Sum(b => b!.AdjustmentsUsd ?? 0m);
        totals[^3] = active.Sum(b => b!.InvoiceUsd ?? 0m);
        totals[^2] = active.Sum(b => b!.OwnerEarningUsd ?? 0m);
        totals[^1] = active.Sum(b => b!.OwnerEarningPkr ?? 0m);
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Payroll", columns, lines, totals), "payroll", Iso(run.Period.Start), run.IsDraft ? "draft" : null);
    }

    // ===================== Register (bank upload: full IBAN) =====================

    public static ExportFile Register(ExportContext context, PayrollRunHeader run, IReadOnlyList<RegisterRow> rows)
    {
        var columns = new List<ExportColumn<RegisterRow>>
        {
            new("Code", CellKind.Text, r => r.PersonCode),
            new("Name", CellKind.Text, r => r.PersonName),
            new("Bank", CellKind.Text, r => r.BankName),
            new("IBAN", CellKind.Text, r => r.Iban),
            new("Net pay (PKR)", CellKind.Pkr, r => r.NetPayPkr),
        };
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Register", columns, rows, ["Total", null, null, null, rows.Sum(r => r.NetPayPkr ?? 0m)]),
            "register", Iso(run.Period.Start), run.IsDraft ? "draft" : null);
    }

    // ===================== Invoice (Admin) =====================

    public static IReadOnlyList<ExportFilter> InvoiceFilters(InvoiceDetails details)
    {
        var inv = details.Invoice;
        return
        [
            new("Invoice", inv.Number),
            new("Status", InvoiceDisplay.Label(inv.Status, details.IsOverdue)),
            new("Period", DisplayFormat.DateRange(inv.PeriodStart, inv.PeriodEnd)),
            new("Issue date", DisplayFormat.Date(inv.IssueDate)),
            new("Due date", DisplayFormat.Date(inv.DueDate)),
        ];
    }

    public static ExportFile Invoice(ExportContext context, Invoice invoice)
    {
        var columns = new List<ExportColumn<InvoiceLine>>
        {
            new("#", CellKind.Integer, l => l.Position),
            new("Name", CellKind.Text, l => l.Name),
            new("Designation", CellKind.Text, l => l.Designation),
            new("Days", CellKind.Text, l => l.DaysText),
            new("Salary (USD)", CellKind.Usd, l => l.SalaryUsd),
            new("Extras (USD)", CellKind.Usd, l => l.ExtrasUsd),
            new("Amount (USD)", CellKind.Usd, l => l.AmountUsd),
        };
        var lines = invoice.Lines.OrderBy(l => l.Position).ToList();
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Invoice", columns, lines,
            ["Total (USD)", null, null, null, lines.Sum(l => l.SalaryUsd), lines.Sum(l => l.ExtrasUsd), invoice.TotalUsd]), invoice.Number);
    }

    // ===================== Owner income (Admin) =====================

    public static ExportFile OwnerIncome(ExportContext context, OwnerIncomeViewModel model)
    {
        var r = model.Report;
        var lines = new List<(string Label, decimal Usd, decimal Pkr, decimal? DraftUsd, decimal? DraftPkr)>
        {
            ("Own salary", r.Selection.SalaryUsd, r.Selection.SalaryPkr, r.DraftSelection?.SalaryUsd, r.DraftSelection?.SalaryPkr),
            ("Commission", r.Selection.CommissionUsd, r.Selection.CommissionPkr, r.DraftSelection?.CommissionUsd, r.DraftSelection?.CommissionPkr),
            ("Margin", r.Selection.MarginUsd, r.Selection.MarginPkr, r.DraftSelection?.MarginUsd, r.DraftSelection?.MarginPkr),
        };
        var columns = new List<ExportColumn<(string Label, decimal Usd, decimal Pkr, decimal? DraftUsd, decimal? DraftPkr)>>
        {
            new("Income", CellKind.Text, l => l.Label),
            new("Finalized (USD)", CellKind.Usd, l => l.Usd),
            new("Finalized (PKR)", CellKind.Pkr, l => l.Pkr),
        };
        var totals = new List<object?> { "Total", r.Selection.TotalUsd, r.Selection.TotalPkr };
        if (r.DraftSelection is { } draft)
        {
            columns.Add(new("Draft, projected (USD)", CellKind.Usd, l => l.DraftUsd));
            columns.Add(new("Draft, projected (PKR)", CellKind.Pkr, l => l.DraftPkr));
            totals.Add(draft.TotalUsd);
            totals.Add(draft.TotalPkr);
        }

        var contributors = new List<ExportColumn<IncomeContributor>>
        {
            new("Name", CellKind.Text, c => c.PersonName),
            new("Hire source", CellKind.Text, c => HireSourceDisplay.Label(c.Source)),
            new("Periods", CellKind.Integer, c => c.Periods),
            new("Earning (USD)", CellKind.Usd, c => c.EarningUsd),
            new("Earning (PKR)", CellKind.Pkr, c => c.EarningPkr),
            new("Share %", CellKind.Percent, c => c.SharePercent),
        };
        var builder = new SpreadsheetBuilder(context)
            .AddSheet("Owner income", columns, lines, totals)
            .AddSheet("Contributors", contributors, r.Contributors,
                ["Total", null, null, r.Contributors.Sum(c => c.EarningUsd), r.Contributors.Sum(c => c.EarningPkr), null]);
        return Xlsx(builder, "owner-income", model.View.ToString().ToLowerInvariant(), Iso(model.Anchor));
    }

    // ===================== Reports =====================

    private static List<ExportColumn<PayrollHistoryRow>> HistoryColumns() =>
    [
        new("Period start", CellKind.Date, h => h.PeriodStart),
        new("Period end", CellKind.Date, h => h.PeriodEnd),
        new("People", CellKind.Integer, h => h.People),
        new("Net pay (PKR)", CellKind.Pkr, h => h.NetPayPkr),
        new("Finalized", CellKind.Date, h => h.FinalizedAt),
    ];

    public static ExportFile PayrollHistory(ExportContext context, IReadOnlyList<PayrollHistoryRow> rows, DateOnly today) =>
        Xlsx(new SpreadsheetBuilder(context).AddSheet("Payroll history", HistoryColumns(), rows, ["Total", null, null, rows.Sum(h => h.NetPayPkr), null]),
            "payroll-history", Iso(today));

    public static ExportFile PayrollHistoryAdmin(ExportContext context, IReadOnlyList<AdminPayrollHistoryRow> rows, DateOnly today)
    {
        var columns = HistoryColumns()
            .Select(c => new ExportColumn<AdminPayrollHistoryRow>(c.Header, c.Kind, h => c.Value(h.Row)))
            .Concat(
            [
                new("Invoice (USD)", CellKind.Usd, h => h.InvoiceUsd),
                new("Owner earning (USD)", CellKind.Usd, h => h.OwnerEarningUsd),
                new("Owner earning (PKR)", CellKind.Pkr, h => h.OwnerEarningPkr),
            ])
            .ToList();
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Payroll history", columns, rows,
                ["Total", null, null, rows.Sum(h => h.Row.NetPayPkr), null, rows.Sum(h => h.InvoiceUsd), rows.Sum(h => h.OwnerEarningUsd), rows.Sum(h => h.OwnerEarningPkr)]),
            "payroll-history", Iso(today));
    }

    private static List<ExportColumn<SalaryChangeRow>> ChangeColumns() =>
    [
        new("Effective from", CellKind.Date, c => c.Change.Current.EffectiveFrom),
        new("Code", CellKind.Text, c => c.Code),
        new("Name", CellKind.Text, c => c.FullName),
        new("Designation", CellKind.Text, c => c.Designation),
        new("Old pay (USD)", CellKind.Usd, c => Usd(c.Change.Previous.PayMonthlyAmount, c.Change.Previous.PayCurrency)),
        new("Old pay (PKR)", CellKind.Pkr, c => Pkr(c.Change.Previous.PayMonthlyAmount, c.Change.Previous.PayCurrency)),
        new("New pay (USD)", CellKind.Usd, c => Usd(c.Change.Current.PayMonthlyAmount, c.Change.Current.PayCurrency)),
        new("New pay (PKR)", CellKind.Pkr, c => Pkr(c.Change.Current.PayMonthlyAmount, c.Change.Current.PayCurrency)),
        new("Change %", CellKind.Percent, c => c.Change.PayChangePercent),
    ];

    public static ExportFile SalaryChanges(ExportContext context, IReadOnlyList<SalaryChangeRow> rows, DateOnly from, DateOnly to) =>
        Xlsx(new SpreadsheetBuilder(context).AddSheet("Salary changes", ChangeColumns(), rows), "salary-changes", Iso(from), Iso(to));

    public static ExportFile SalaryChangesAdmin(ExportContext context, IReadOnlyList<SalaryChangeRow> rows, DateOnly from, DateOnly to)
    {
        var columns = ChangeColumns()
            .Concat(
            [
                new("Old billed monthly (USD)", CellKind.Usd, c => c.Change.Previous.BilledMonthlyUsd),
                new("New billed monthly (USD)", CellKind.Usd, c => c.Change.Current.BilledMonthlyUsd),
                new("Old commission (USD)", CellKind.Usd, c => c.Change.Previous.CommissionPerPeriodUsd),
                new("New commission (USD)", CellKind.Usd, c => c.Change.Current.CommissionPerPeriodUsd),
                new("Changed", CellKind.Text, c => c.Change.PayChanged && c.Change.BillingChanged ? "Pay and billing" : c.Change.PayChanged ? "Pay" : "Billing"),
            ])
            .ToList();
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Salary changes", columns, rows), "salary-changes", Iso(from), Iso(to));
    }

    public static ExportFile Headcount(ExportContext context, IReadOnlyList<HeadcountMonth> months, DateOnly today)
    {
        var columns = new List<ExportColumn<HeadcountMonth>>
        {
            new("Month", CellKind.Date, m => m.Month),
            new("As of", CellKind.Date, m => m.AsOf),
            new("Active", CellKind.Integer, m => m.Active),
            new("Employees", CellKind.Integer, m => m.ActiveEmployees),
            new("Internees", CellKind.Integer, m => m.ActiveInternees),
            new("Joiners", CellKind.Integer, m => m.Joiners),
            new("Leavers", CellKind.Integer, m => m.Leavers),
        };
        return Xlsx(new SpreadsheetBuilder(context).AddSheet("Headcount", columns, months,
            ["Total", null, null, null, null, months.Sum(m => m.Joiners), months.Sum(m => m.Leavers)]), "headcount", Iso(today));
    }
}
