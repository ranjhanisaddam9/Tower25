using System.Linq.Expressions;

namespace HR.Domain.People;

/// <summary>
/// The one definition of "active" (owner decision, M7): a person is active until their leaving date has passed, i.e.
/// no leaving date or a leaving date on or after today (Asia/Karachi). Nothing is stored; queries use these
/// expressions so the database and the domain agree.
/// </summary>
public static class PersonStatus
{
    public static Expression<Func<Person, bool>> ActiveOn(DateOnly today) =>
        p => p.LeavingDate == null || p.LeavingDate >= today;

    public static Expression<Func<Person, bool>> InactiveOn(DateOnly today) =>
        p => p.LeavingDate != null && p.LeavingDate < today;

    public static bool IsActive(DateOnly? leavingDate, DateOnly today) => leavingDate is not { } leaving || leaving >= today;

    /// <summary>True while a leaving date is set but hasn't passed (shown as a "Leaving …" pill).</summary>
    public static bool IsLeaving(DateOnly? leavingDate, DateOnly today) => leavingDate is { } leaving && leaving >= today;
}
