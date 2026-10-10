using System.Globalization;
using HR.Web.Formatting;

namespace HR.Web.ViewModels;

public enum ChartUnit
{
    Pkr,
    Usd,
    Count,
}

/// <summary>One series of a chart. <see cref="Css"/> picks its colour from theme tokens (no style attributes).</summary>
public sealed record ChartSeries(string Css, string Label, IReadOnlyList<decimal> Values);

/// <summary>
/// A server-rendered (stacked) bar chart (M9 reports). Geometry only: colours come from CSS classes, so both themes keep
/// their contrast; the page shows the same numbers in a data table. Rendered by Shared/_BarChart.
/// </summary>
public sealed record BarChartViewModel(string Id, string Title, string Description, IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series, ChartUnit Unit)
{
    public const int Width = 720;
    public const int Height = 280;
    public const int Left = 72;
    public const int Right = 12;
    public const int Top = 16;
    public const int Bottom = 36;

    public int PlotWidth => Width - Left - Right;

    public int PlotHeight => Height - Top - Bottom;

    public decimal Total(int index) => Series.Sum(s => Math.Max(0m, s.Values[index]));

    /// <summary>A rounded axis maximum (1, 2, 2.5 or 5 × a power of ten) at or above the tallest bar.</summary>
    public decimal AxisMax
    {
        get
        {
            var max = Labels.Count == 0 ? 0m : Enumerable.Range(0, Labels.Count).Max(Total);
            if (max <= 0m)
            {
                return Unit == ChartUnit.Count ? 4m : 100m;
            }

            // People counts: a multiple of 4, so the four gridlines are whole numbers.
            if (Unit == ChartUnit.Count)
            {
                return Math.Max(4m, Math.Ceiling(max / 4m) * 4m);
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

    public decimal Slot => Labels.Count == 0 ? PlotWidth : PlotWidth / (decimal)Labels.Count;

    public decimal BarWidth => Slot * 0.62m;

    public decimal BarX(int index) => Left + index * Slot + (Slot - BarWidth) / 2m;

    public decimal Scale(decimal value) => Math.Max(0m, value) / AxisMax * PlotHeight;

    /// <summary>With many bars only every n-th label is drawn, so they never overlap.</summary>
    public int LabelEvery => Math.Max(1, (int)Math.Ceiling(Labels.Count / 12m));

    public static string F(decimal value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>Compact axis labels: "Rs 2.5M", "$5k", "12".</summary>
    public string Axis(decimal value)
    {
        string Compact(decimal v) => v >= 1_000_000m ? (v / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : v >= 1_000m ? (v / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : v.ToString("0.#", CultureInfo.InvariantCulture);
        return Unit switch
        {
            ChartUnit.Pkr => "Rs " + Compact(value),
            ChartUnit.Usd => "$" + Compact(value),
            _ => Compact(value),
        };
    }

    public string Value(decimal value) => Unit switch
    {
        ChartUnit.Pkr => DisplayFormat.Pkr(value),
        ChartUnit.Usd => DisplayFormat.Usd(value),
        _ => value.ToString("0.#", CultureInfo.InvariantCulture),
    };
}

/// <summary>A tiny trend line for the dashboard (decorative; the card states the numbers in text).</summary>
public sealed record SparklineViewModel(string Css, IReadOnlyList<decimal> Values)
{
    public const int Width = 160;
    public const int Height = 40;
    private const int Pad = 4;

    public string Points
    {
        get
        {
            if (Values.Count == 0)
            {
                return string.Empty;
            }

            var min = Values.Min();
            var max = Values.Max();
            var span = max - min == 0m ? 1m : max - min;
            var step = Values.Count == 1 ? 0m : (Width - 2m * Pad) / (Values.Count - 1);
            return string.Join(' ', Values.Select((v, i) =>
                BarChartViewModel.F(Pad + i * step) + "," + BarChartViewModel.F(Height - Pad - (max - min == 0m ? (Height - 2m * Pad) / 2m : (v - min) / span * (Height - 2m * Pad)))));
        }
    }
}
