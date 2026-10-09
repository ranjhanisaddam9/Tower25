using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HR.Domain.Rates;
using HR.Infrastructure.Rates;
using HR.Web.Formatting;

namespace HR.Web.ViewModels;

public sealed class ExchangeRateFormViewModel
{
    [Required(ErrorMessage = "Enter the date the rate takes effect.")]
    [DataType(DataType.Date)]
    [Display(Name = "Effective from")]
    public DateOnly? EffectiveFrom { get; set; }

    [Required(ErrorMessage = "Enter the rate.")]
    [Range(typeof(decimal), "100", "1000", ErrorMessage = "The rate must be between 100.0000 and 1000.0000 PKR per USD.")]
    [Display(Name = "PKR for 1 USD")]
    public decimal? UsdToPkr { get; set; }

    [StringLength(ExchangeRateRules.NoteMaxLength, ErrorMessage = "The note can be at most 200 characters.")]
    [Display(Name = "Note (optional)")]
    public string? Note { get; set; }

    /// <summary>"I've double-checked this rate": required when the rate moves more than 5% from the entry before it.</summary>
    [Display(Name = "I've double-checked this rate")]
    public bool ConfirmLargeChange { get; set; }

    public string? RowVersion { get; set; }

    public RateInput ToInput() => new(EffectiveFrom, UsdToPkr, Note);

    public static ExchangeRateFormViewModel From(RateDetails r) => new()
    {
        EffectiveFrom = r.EffectiveFrom,
        UsdToPkr = r.UsdToPkr,
        Note = r.Note,
        RowVersion = Convert.ToBase64String(r.RowVersion),
    };
}

/// <summary>Create/edit page model. <see cref="Id"/> is null on create.</summary>
public sealed record ExchangeRateEditViewModel(
    int? Id,
    ExchangeRateFormViewModel Form,
    LargeChange? LargeChange = null,
    IReadOnlyList<FieldChangeViewModel>? ConflictChanges = null);

/// <summary>"Up 0.75 (+0.27%)": direction is always in words and an icon, never colour alone.</summary>
public sealed record RateChangeViewModel(decimal Absolute, decimal Percent)
{
    public string Direction => Absolute switch { > 0 => "Up", < 0 => "Down", _ => "No change" };

    public string Icon => Absolute switch { > 0 => "bi-arrow-up-right", < 0 => "bi-arrow-down-right", _ => "bi-dash" };

    public string Tone => Absolute switch { > 0 => "up", < 0 => "down", _ => "flat" };

    public string Text => Absolute == 0
        ? "No change"
        : $"{Direction} {ExchangeRateRules.Format(Math.Abs(Absolute))} ({(Percent > 0 ? "+" : "−")}{Math.Abs(Percent).ToString("0.00", CultureInfo.InvariantCulture)}%)";

    public static RateChangeViewModel? Between(decimal? previous, decimal current) =>
        previous is { } p ? new RateChangeViewModel(current - p, ExchangeRateRules.PercentChange(p, current)) : null;
}

public sealed record RateRowViewModel(
    int Id,
    DateOnly EffectiveFrom,
    decimal UsdToPkr,
    RateChangeViewModel? Change,
    string? Note,
    string? AddedBy,
    bool IsScheduled);

public sealed record RateLookupViewModel(DateOnly Date, RateSnapshot? Result);

public sealed record ExchangeRatesPageViewModel(
    RateSnapshot? Current,
    RateChangeViewModel? CurrentChange,
    RateSnapshot? Previous,
    IReadOnlyList<RateRowViewModel> Rows,
    int Page,
    int TotalPages,
    int TotalCount,
    RateChartViewModel? Chart,
    DateOnly LookupDefault,
    RateLookupViewModel? Lookup);

/// <summary>
/// A server-rendered SVG line chart. Coordinates are computed here; the view emits only SVG presentation
/// attributes and CSS classes (no style attributes, no script), so it satisfies the CSP.
/// </summary>
public sealed record RateChartViewModel(
    IReadOnlyList<RateChartPoint> Points,
    string Polyline,
    decimal Min,
    decimal Max,
    string Title,
    string Summary)
{
    public const int Width = 640;
    public const int Height = 220;
    public const int PadLeft = 84; // room for the y labels, which are drawn larger on phones
    public const int PadRight = 20;
    public const int PadTop = 20;
    public const int PadBottom = 36;

    public static int PlotBottom => Height - PadBottom;

    public static int PlotRight => Width - PadRight;

    public static RateChartViewModel? Build(IReadOnlyList<RateSnapshot> entries, DateOnly today)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var min = entries.Min(e => e.UsdToPkr);
        var max = entries.Max(e => e.UsdToPkr);
        var span = max - min;
        var low = span == 0 ? min - 1 : min - span * 0.1m;
        var high = span == 0 ? max + 1 : max + span * 0.1m;

        double X(int i) => entries.Count == 1
            ? (PadLeft + PlotRight) / 2.0
            : PadLeft + i * (PlotRight - PadLeft) / (double)(entries.Count - 1);
        double Y(decimal v) => PadTop + (double)((high - v) / (high - low)) * (PlotBottom - PadTop);

        var points = entries
            .Select((e, i) => new RateChartPoint(
                Math.Round(X(i), 1),
                Math.Round(Y(e.UsdToPkr), 1),
                e.EffectiveFrom,
                e.UsdToPkr,
                e.EffectiveFrom > today))
            .ToList();
        var polyline = string.Join(' ', points.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y}")));

        var first = entries[0];
        var last = entries[^1];
        var title = $"USD to PKR, last {entries.Count} {(entries.Count == 1 ? "entry" : "entries")}";
        var summary = entries.Count == 1
            ? $"One entry: Rs {ExchangeRateRules.Format(first.UsdToPkr)} from {DisplayFormat.Date(first.EffectiveFrom)}."
            : $"From Rs {ExchangeRateRules.Format(first.UsdToPkr)} on {DisplayFormat.Date(first.EffectiveFrom)} to Rs {ExchangeRateRules.Format(last.UsdToPkr)} on {DisplayFormat.Date(last.EffectiveFrom)}. Lowest Rs {ExchangeRateRules.Format(min)}, highest Rs {ExchangeRateRules.Format(max)}.";

        return new RateChartViewModel(points, polyline, min, max, title, summary);
    }

    /// <summary>The y coordinate of a value, for the min/max grid lines.</summary>
    public double YOf(decimal value) => Points.Count == 0 ? PlotBottom : Points.First(p => p.Value == value).Y;
}

public sealed record RateChartPoint(double X, double Y, DateOnly Date, decimal Value, bool IsScheduled)
{
    public string Xs => X.ToString(CultureInfo.InvariantCulture);

    public string Ys => Y.ToString(CultureInfo.InvariantCulture);
}
