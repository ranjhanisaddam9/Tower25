using System.Globalization;
using HR.Domain.Reports;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Reports;
using HR.Web.Formatting;

namespace HR.Web.ViewModels;

public static class ReportDisplay
{
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string PeriodShort(DateOnly start) => start.ToString("d MMM yy", CultureInfo.InvariantCulture);

    public static string MonthShort(DateOnly month) => month.ToString("MMM yy", CultureInfo.InvariantCulture);

    public static string MonthLong(DateOnly month) => month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}

/// <summary>Payroll history (M9). <see cref="Admin"/> and <see cref="EarningChart"/> are null for Managers (never loaded).</summary>
public sealed record PayrollHistoryViewModel(
    IReadOnlyList<PayrollHistoryRow> Rows,
    IReadOnlyList<AdminPayrollHistoryRow>? Admin,
    BarChartViewModel NetPayChart,
    BarChartViewModel? EarningChart)
{
    public static BarChartViewModel NetPay(IEnumerable<PayrollHistoryRow> newestFirst)
    {
        var rows = newestFirst.Take(ReportService.HistoryChartPeriods).Reverse().ToList();
        return new BarChartViewModel("netPayChart", "Net pay by payroll period", $"Bars of total net pay in rupees for the last {rows.Count} finalized payroll periods, oldest first.",
            rows.Select(r => ReportDisplay.PeriodShort(r.PeriodStart)).ToList(),
            [new ChartSeries("chart-salary", "Net pay (PKR)", rows.Select(r => r.NetPayPkr).ToList())], ChartUnit.Pkr);
    }

    public static BarChartViewModel Earnings(IEnumerable<AdminPayrollHistoryRow> newestFirst)
    {
        var rows = newestFirst.Take(ReportService.HistoryChartPeriods).Reverse().ToList();
        return new BarChartViewModel("earningChart", "Owner earning by payroll period", $"Bars of owner earning in US dollars for the last {rows.Count} finalized payroll periods, oldest first.",
            rows.Select(r => ReportDisplay.PeriodShort(r.Row.PeriodStart)).ToList(),
            [new ChartSeries("chart-commission", "Owner earning (USD)", rows.Select(r => r.OwnerEarningUsd).ToList())], ChartUnit.Usd);
    }
}

public sealed record SalaryChangesViewModel(DateOnly From, DateOnly To, IReadOnlyList<SalaryChangeRow> Rows, bool ShowBilling, string? Error);

public sealed record AbsenceSummaryViewModel(DateOnly From, DateOnly To, AbsenceRange? Range, string? Error);

public sealed record HeadcountViewModel(IReadOnlyList<HeadcountMonth> Months)
{
    public BarChartViewModel Chart => new("headcountChart", "Active people at month end", "Stacked bars of active employees and internees at the end of each of the last 12 months (today for the current month).",
        Months.Select(m => ReportDisplay.MonthShort(m.Month)).ToList(),
        [
            new ChartSeries("chart-salary", "Employees", Months.Select(m => (decimal)m.ActiveEmployees).ToList()),
            new ChartSeries("chart-commission", "Internees", Months.Select(m => (decimal)m.ActiveInternees).ToList()),
        ],
        ChartUnit.Count);
}

/// <summary>The dashboard "Payroll trend" card. <see cref="EarningUsd"/> is null for Managers (never loaded).</summary>
public sealed record PayrollTrendViewModel(IReadOnlyList<PayrollHistoryRow> OldestFirst, IReadOnlyList<decimal>? EarningUsd)
{
    public SparklineViewModel NetPay => new("spark-netpay", OldestFirst.Select(r => r.NetPayPkr).ToList());

    public SparklineViewModel? Earning => EarningUsd is { } e ? new SparklineViewModel("spark-earning", e) : null;

    public string Summary
    {
        get
        {
            if (OldestFirst.Count == 0)
            {
                return "No finalized payroll yet.";
            }

            var latest = OldestFirst[^1];
            var text = $"Latest finalized payroll ({DisplayFormat.DateRange(latest.PeriodStart, latest.PeriodEnd)}): net pay {DisplayFormat.Pkr(latest.NetPayPkr)}";
            if (OldestFirst.Count > 1)
            {
                var first = OldestFirst[0];
                text += $"; {DisplayFormat.Pkr(first.NetPayPkr)} in {DisplayFormat.DateRange(first.PeriodStart, first.PeriodEnd)}";
            }

            if (EarningUsd is { Count: > 0 } earning)
            {
                text += $". Owner earning {DisplayFormat.Usd(earning[^1])} in the latest period";
            }

            return text + ".";
        }
    }
}
