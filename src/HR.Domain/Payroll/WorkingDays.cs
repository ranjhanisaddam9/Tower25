namespace HR.Domain.Payroll;

/// <summary>
/// Working days are Monday–Friday calendar dates (SPEC §3). There is no holiday calendar in v1.
/// </summary>
public static class WorkingDays
{
    public static bool IsWorkingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// Counts Monday–Friday dates from <paramref name="start"/> to <paramref name="end"/>, both inclusive.
    /// An empty range (end before start) has 0 working days.
    /// </summary>
    public static int Count(DateOnly start, DateOnly end)
    {
        var count = 0;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (IsWorkingDay(date))
            {
                count++;
            }
        }

        return count;
    }
}
