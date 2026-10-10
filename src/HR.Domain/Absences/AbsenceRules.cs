using HR.Domain.Payroll;
using HR.Domain.People;

namespace HR.Domain.Absences;

/// <summary>The SPEC §4 date rules for recording an absence. Locked periods are checked by the caller (they need the payroll).</summary>
public static class AbsenceRules
{
    public const string WeekendMessage = "Absences can only be recorded on working days (Monday to Friday).";
    public const string NotEmployedMessage = "This person wasn't employed on that date.";
    public const string DuplicateMessage = "An absence is already recorded for this person on that date.";
    public const string TooFarAheadMessage = "Absences can be recorded at most one year ahead.";
    public const string LockedMessage = "That date is part of a finalized payroll, so its absences can't be changed.";
    public const string NoteTooLongMessage = "The note can be at most 300 characters.";

    /// <summary>The most days a range entry may cover, inclusive.</summary>
    public const int MaxRangeDays = 31;

    public static bool IsWeekday(DateOnly date) => WorkingDays.IsWorkingDay(date);

    /// <summary>The latest date an absence may be recorded on: one year after today.</summary>
    public static DateOnly LatestAllowed(DateOnly today) => today.AddYears(1);

    /// <summary>
    /// The first rule <paramref name="date"/> breaks, or null when it may hold an absence. Checked in this order:
    /// weekend, more than a year ahead, outside every employment period, already recorded.
    /// </summary>
    public static string? DateError(DateOnly date, DateOnly today, IEnumerable<EmploymentSpan> employment, bool alreadyRecorded)
    {
        if (!IsWeekday(date))
        {
            return WeekendMessage;
        }

        if (date > LatestAllowed(today))
        {
            return TooFarAheadMessage;
        }

        if (!EmploymentCalendar.IsEmployedOn(employment, date))
        {
            return NotEmployedMessage;
        }

        return alreadyRecorded ? DuplicateMessage : null;
    }
}
