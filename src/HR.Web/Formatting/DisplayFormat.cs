using System.Globalization;
using HR.Domain.Time;

namespace HR.Web.Formatting;

/// <summary>Display formats from CLAUDE.md: USD $1,234.56, PKR Rs 1,234,567, dates 08 Oct 2026.</summary>
public static class DisplayFormat
{
    private static readonly CultureInfo UsdCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo PkrCulture = CultureInfo.GetCultureInfo("en-PK");

    public static string Usd(decimal amount) =>
        (amount < 0 ? "-$" : "$") + Math.Abs(amount).ToString("N2", UsdCulture);

    /// <summary>Whole rupees; the value is expected to be rounded already (Money.RoundPkr).</summary>
    public static string Pkr(decimal amount) =>
        (amount < 0 ? "-Rs " : "Rs ") + Math.Abs(amount).ToString("N0", PkrCulture);

    /// <summary>An amount in its pay currency: "$1,234.56" or "Rs 196,000".</summary>
    public static string Money(decimal amount, HR.Domain.Pay.PayCurrency currency) =>
        currency == HR.Domain.Pay.PayCurrency.USD ? Usd(amount) : Pkr(amount);

    /// <summary>A signed percentage with 2 decimals: "+7.14%", "−3.50%", "0.00%".</summary>
    public static string Percent(decimal percent) =>
        (percent > 0 ? "+" : percent < 0 ? "−" : "") + Math.Abs(percent).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    public static string Date(DateOnly date) =>
        date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A UTC timestamp shown in Asia/Karachi time: "08 Oct 2026, 14:05".</summary>
    public static string DateTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, PakistanTime.Zone).ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A date range, compact when both ends share a month: "01–15 Oct 2026".</summary>
    public static string DateRange(DateOnly start, DateOnly end)
    {
        if (start.Year == end.Year && start.Month == end.Month)
        {
            return start.ToString("dd", CultureInfo.InvariantCulture) + "–" + Date(end);
        }

        return Date(start) + " – " + Date(end);
    }
}
