using HR.Domain.Payroll;

namespace HR.Domain.People;

/// <summary>Employed working days (SPEC §3): Monday–Friday dates that fall inside any of a person's employment periods.</summary>
public static class EmploymentCalendar
{
    /// <summary>Counts the Mon–Fri dates from <paramref name="rangeStart"/> to <paramref name="rangeEnd"/> (inclusive) inside any period.</summary>
    public static int EmployedWorkingDays(IEnumerable<EmploymentSpan> periods, DateOnly rangeStart, DateOnly rangeEnd)
    {
        var spans = periods.ToList();
        var count = 0;
        for (var date = rangeStart; date <= rangeEnd; date = date.AddDays(1))
        {
            if (WorkingDays.IsWorkingDay(date) && spans.Any(s => s.Contains(date)))
            {
                count++;
            }
        }

        return count;
    }

    public static int EmployedWorkingDays(IEnumerable<EmploymentPeriod> periods, DateOnly rangeStart, DateOnly rangeEnd) =>
        EmployedWorkingDays(periods.Select(p => p.Span), rangeStart, rangeEnd);

    public static bool IsEmployedOn(IEnumerable<EmploymentSpan> periods, DateOnly date) => periods.Any(s => s.Contains(date));

    public static bool IsEmployedOn(IEnumerable<EmploymentPeriod> periods, DateOnly date) => IsEmployedOn(periods.Select(p => p.Span), date);
}
