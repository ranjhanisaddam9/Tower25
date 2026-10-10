using HR.Domain.People;

namespace HR.Domain.Reports;

/// <summary>A person as the headcount report sees them: their type and every employment period.</summary>
public sealed record HeadcountPerson(PersonType Type, IReadOnlyList<EmploymentSpan> Employment);

/// <param name="Month">The first day of the month.</param>
/// <param name="AsOf">Month end, or today for the current month.</param>
public sealed record HeadcountMonth(DateOnly Month, DateOnly AsOf, int ActiveEmployees, int ActiveInternees, int Joiners, int Leavers)
{
    public int Active => ActiveEmployees + ActiveInternees;
}

/// <summary>
/// Headcount by month (M9 report). For each month: people employed at month end (or today for the current month),
/// split into employees and internees; joiners = employment periods that started in the month; leavers = periods whose
/// last day fell in the month. Both count only dates up to "as of", so a leaving date later this month isn't a leaver yet.
/// A rejoin is a joiner again (it opens a new employment period, SPEC §2).
/// </summary>
public static class HeadcountCalculator
{
    public static IReadOnlyList<HeadcountMonth> LastMonths(IEnumerable<HeadcountPerson> people, DateOnly today, int months = 12)
    {
        var list = people.ToList();
        var current = new DateOnly(today.Year, today.Month, 1);
        var result = new List<HeadcountMonth>(months);
        for (var i = months - 1; i >= 0; i--)
        {
            var start = current.AddMonths(-i);
            var end = start.AddMonths(1).AddDays(-1);
            var asOf = end < today ? end : today;

            int Active(PersonType type) => list.Count(p => p.Type == type && p.Employment.Any(s => s.Contains(asOf)));
            var spans = list.SelectMany(p => p.Employment).ToList();
            result.Add(new HeadcountMonth(
                start,
                asOf,
                Active(PersonType.Employee),
                Active(PersonType.Internee),
                spans.Count(s => s.Start >= start && s.Start <= asOf),
                spans.Count(s => s.End is { } e && e >= start && e <= asOf)));
        }

        return result;
    }
}
