namespace HR.Domain.Payroll;

/// <summary>
/// A half-month pay period: the 1st–15th or the 16th–last day of the month (SPEC §3).
/// </summary>
public sealed record PayPeriod
{
    private PayPeriod(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    /// <summary>True for the 1st–15th period, false for the 16th–month-end period.</summary>
    public bool IsFirstHalf => Start.Day == 1;

    public int WorkingDayCount => WorkingDays.Count(Start, End);

    /// <summary>The period that contains <paramref name="date"/>.</summary>
    public static PayPeriod For(DateOnly date)
    {
        if (date.Day <= 15)
        {
            return new PayPeriod(new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 15));
        }

        var lastDay = DateTime.DaysInMonth(date.Year, date.Month);
        return new PayPeriod(new DateOnly(date.Year, date.Month, 16), new DateOnly(date.Year, date.Month, lastDay));
    }

    public PayPeriod Next() => For(End.AddDays(1));

    public PayPeriod Previous() => For(Start.AddDays(-1));

    public bool Contains(DateOnly date) => date >= Start && date <= End;
}
