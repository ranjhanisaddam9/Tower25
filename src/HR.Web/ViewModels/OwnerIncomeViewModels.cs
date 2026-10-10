using System.Globalization;
using HR.Domain.Payroll;
using HR.Infrastructure.Payroll;
using HR.Web.Formatting;

namespace HR.Web.ViewModels;

public sealed record OwnerIncomeViewModel(OwnerIncomeReport Report, bool IncludeDraft)
{
    public IncomeView View => Report.View;

    public DateOnly Anchor => Report.Anchor;

    public string SelectionLabel => View switch
    {
        IncomeView.Period => DisplayFormat.DateRange(PayPeriod.For(Anchor).Start, PayPeriod.For(Anchor).End),
        IncomeView.Month => Anchor.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        _ => Anchor.Year.ToString(CultureInfo.InvariantCulture),
    };

    public string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public Dictionary<string, string> Route(IncomeView view, DateOnly at)
    {
        var route = new Dictionary<string, string> { ["view"] = view.ToString(), ["at"] = Iso(at) };
        if (IncludeDraft)
        {
            route["draft"] = "true";
        }

        return route;
    }

    public OwnerIncomeChart Chart => new(Report.YearByMonth, Report.DraftYearByMonth, Anchor.Year);
}

/// <summary>
/// The server-rendered stacked bar chart (by month: own salary, commission, margin, and optional draft on top). Geometry
/// only: colours come from CSS classes so both themes keep their contrast.
/// </summary>
public sealed record OwnerIncomeChart(IReadOnlyList<(int Month, IncomeBreakdown Income)> Finalized, IReadOnlyList<(int Month, IncomeBreakdown Income)>? Draft, int Year)
{
    public const int Width = 720;
    public const int Height = 300;
    public const int Left = 64;
    public const int Right = 12;
    public const int Top = 16;
    public const int Bottom = 36;

    public int PlotWidth => Width - Left - Right;

    public int PlotHeight => Height - Top - Bottom;

    public decimal DraftUsd(int month) => Draft?.Single(d => d.Month == month).Income.TotalUsd ?? 0m;

    /// <summary>A rounded axis maximum (1, 2 or 5 × a power of ten) at or above the tallest bar.</summary>
    public decimal AxisMax
    {
        get
        {
            var max = Finalized.Max(m => m.Income.TotalUsd + DraftUsd(m.Month));
            if (max <= 0m)
            {
                return 100m;
            }

            var magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)max)));
            foreach (var step in new[] { 1m, 2m, 2.5m, 5m, 10m })
            {
                if (step * magnitude >= max)
                {
                    return step * magnitude;
                }
            }

            return 10m * magnitude;
        }
    }

    public decimal Slot => PlotWidth / 12m;

    public decimal BarWidth => Slot * 0.62m;

    public decimal BarX(int month) => Left + (month - 1) * Slot + (Slot - BarWidth) / 2m;

    public decimal Scale(decimal usd) => usd / AxisMax * PlotHeight;

    public string F(decimal value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    public static string MonthShort(int month) => new DateOnly(2000, month, 1).ToString("MMM", CultureInfo.InvariantCulture);

    public static string MonthLong(int month) => new DateOnly(2000, month, 1).ToString("MMMM", CultureInfo.InvariantCulture);

    /// <summary>The stacked segments of one month, bottom to top: (css class, label, value).</summary>
    public IEnumerable<(string Css, string Label, decimal Usd)> Segments(int month)
    {
        var income = Finalized.Single(m => m.Month == month).Income;
        yield return ("chart-salary", "Own salary", income.SalaryUsd);
        yield return ("chart-commission", "Commission", income.CommissionUsd);
        yield return ("chart-margin", "Margin", income.MarginUsd);
        if (Draft is not null)
        {
            yield return ("chart-draft", "Draft (projected)", DraftUsd(month));
        }
    }
}
