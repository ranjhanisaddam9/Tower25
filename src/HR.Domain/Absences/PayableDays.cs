using HR.Domain.Payroll;
using HR.Domain.People;

namespace HR.Domain.Absences;

/// <summary>The day counts behind a person's pay for one period (SPEC §3–§5). Payroll (M7) uses exactly this.</summary>
public sealed record PayableDaysResult(int WorkingDays, int EmployedWorkingDays, decimal UnpaidDays)
{
    /// <summary>Employed working days minus unpaid absence days.</summary>
    public decimal PayableDays => EmployedWorkingDays - UnpaidDays;
}

public static class PayableDays
{
    /// <summary>
    /// Counts one period. Only unpaid days on employed working days inside the period count, so an absence left outside
    /// employment (for example after a leaving date moved) never reduces pay twice.
    /// </summary>
    public static PayableDaysResult For(IEnumerable<EmploymentSpan> employment, IEnumerable<AllocatedAbsence> allocated, PayPeriod period)
    {
        var spans = employment.ToList();
        var unpaid = allocated
            .Where(a => period.Contains(a.Date) && WorkingDays.IsWorkingDay(a.Date) && EmploymentCalendar.IsEmployedOn(spans, a.Date))
            .Sum(a => a.UnpaidDays);
        return new PayableDaysResult(period.WorkingDayCount, EmploymentCalendar.EmployedWorkingDays(spans, period.Start, period.End), unpaid);
    }
}
