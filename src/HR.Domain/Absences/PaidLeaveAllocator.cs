namespace HR.Domain.Absences;

/// <summary>One absence as the allocator sees it.</summary>
public readonly record struct AbsenceDay(DateOnly Date, AbsencePortion Portion);

/// <summary>An absence split into its paid and unpaid parts; <see cref="PaidDays"/> + <see cref="UnpaidDays"/> = the portion.</summary>
public readonly record struct AllocatedAbsence(DateOnly Date, AbsencePortion Portion, decimal PaidDays, decimal UnpaidDays)
{
    public decimal Days => Portion.Days();

    public bool IsFullyPaid => UnpaidDays == 0m;

    public bool IsUnpaid => PaidDays == 0m;

    public bool IsPartlyPaid => PaidDays > 0m && UnpaidDays > 0m;
}

/// <summary>
/// Paid leave (SPEC §4): 1.0 paid day per calendar month per person, applied chronologically to the month's absences
/// across both pay periods. Unused leave expires at month end. The result is never stored; it is recomputed from all of
/// a person's absences in the month, so adding, editing or deleting one can change the ones after it.
/// </summary>
public static class PaidLeaveAllocator
{
    public const decimal DaysPerMonth = 1.0m;

    /// <summary>Allocates one person's absences. The input order doesn't matter; the output is sorted by date.</summary>
    public static IReadOnlyList<AllocatedAbsence> Allocate(IEnumerable<AbsenceDay> absences)
    {
        var result = new List<AllocatedAbsence>();
        var month = (Year: 0, Month: 0);
        var left = 0m;
        foreach (var absence in absences.OrderBy(a => a.Date))
        {
            if ((absence.Date.Year, absence.Date.Month) != month)
            {
                month = (absence.Date.Year, absence.Date.Month);
                left = DaysPerMonth;
            }

            var days = absence.Portion.Days();
            var paid = Math.Min(days, left);
            left -= paid;
            result.Add(new AllocatedAbsence(absence.Date, absence.Portion, paid, days - paid));
        }

        return result;
    }

    /// <summary>Paid leave still unused in the month of <paramref name="month"/>, after all of that month's absences.</summary>
    public static decimal LeftInMonth(IEnumerable<AbsenceDay> absences, DateOnly month)
    {
        var used = absences
            .Where(a => a.Date.Year == month.Year && a.Date.Month == month.Month)
            .Sum(a => a.Portion.Days());
        return Math.Max(0m, DaysPerMonth - used);
    }
}
