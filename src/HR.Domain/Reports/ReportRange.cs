namespace HR.Domain.Reports;

/// <summary>Custom date ranges for reports and exports (M9): at most one year (e.g. 01 Jan–31 Dec).</summary>
public static class ReportRange
{
    public const string OrderMessage = "The end date can't be before the start date.";
    public const string TooLongMessage = "Pick a range of at most one year.";

    /// <summary>The latest end date allowed for a range starting on <paramref name="from"/>.</summary>
    public static DateOnly MaxTo(DateOnly from) => from.AddYears(1).AddDays(-1);

    /// <summary>Null when the range is valid, else the message to show.</summary>
    public static string? Validate(DateOnly from, DateOnly to) =>
        to < from ? OrderMessage : to > MaxTo(from) ? TooLongMessage : null;

    /// <summary>The first day of every calendar month the range touches, in order.</summary>
    public static IReadOnlyList<DateOnly> Months(DateOnly from, DateOnly to)
    {
        var months = new List<DateOnly>();
        for (var m = new DateOnly(from.Year, from.Month, 1); m <= to; m = m.AddMonths(1))
        {
            months.Add(m);
        }

        return months;
    }
}
