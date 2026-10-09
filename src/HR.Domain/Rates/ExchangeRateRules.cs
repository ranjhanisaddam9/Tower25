using System.Globalization;

namespace HR.Domain.Rates;

/// <summary>Validation, change detection and display for USD→PKR rates.</summary>
public static class ExchangeRateRules
{
    public const decimal Min = 100.0000m;
    public const decimal Max = 1000.0000m;
    public const int MaxDecimals = 4;
    public const int NoteMaxLength = 200;

    /// <summary>A change above this fraction of the preceding rate needs an explicit "double-checked" confirmation.</summary>
    public const decimal LargeChangeThreshold = 0.05m;

    /// <summary>Null when valid, otherwise a friendly message.</summary>
    public static string? RateError(decimal usdToPkr)
    {
        if (usdToPkr < Min || usdToPkr > Max)
        {
            return "The rate must be between 100.0000 and 1000.0000 PKR per USD.";
        }

        return usdToPkr.Scale > MaxDecimals && decimal.Round(usdToPkr, MaxDecimals) != usdToPkr
            ? "Use at most 4 decimal places."
            : null;
    }

    /// <summary>True when <paramref name="next"/> differs from <paramref name="previous"/> by MORE than 5% (exactly 5% is not large).</summary>
    public static bool IsLargeChange(decimal previous, decimal next) =>
        previous > 0 && Math.Abs(next - previous) > previous * LargeChangeThreshold;

    /// <summary>Change from previous to next as a percentage of previous (e.g. 0.27 for +0.27%).</summary>
    public static decimal PercentChange(decimal previous, decimal next) =>
        previous == 0 ? 0 : (next - previous) / previous * 100m;

    /// <summary>Up to 4 decimals with trailing zeros trimmed, at least 2: 280.5 → "280.50", 280.1234 → "280.1234".</summary>
    public static string Format(decimal usdToPkr) =>
        usdToPkr.ToString("#,##0.00##", CultureInfo.InvariantCulture);
}

/// <summary>The rate in effect on a date: the entry with the latest EffectiveFrom on or before it.</summary>
public static class RateTimeline
{
    /// <summary>
    /// Works on any IQueryable, so the same rule runs in the database (EF Core) and in memory (unit tests).
    /// Returns null before the first entry; future-dated entries are ignored until their date.
    /// </summary>
    public static ExchangeRate? RateOn(this IQueryable<ExchangeRate> rates, DateOnly date) =>
        rates.Where(r => r.EffectiveFrom <= date).OrderByDescending(r => r.EffectiveFrom).FirstOrDefault();

    /// <summary>The query behind <see cref="RateOn"/>, for async execution by EF Core.</summary>
    public static IQueryable<ExchangeRate> InEffectOn(this IQueryable<ExchangeRate> rates, DateOnly date) =>
        rates.Where(r => r.EffectiveFrom <= date).OrderByDescending(r => r.EffectiveFrom).Take(1);
}
