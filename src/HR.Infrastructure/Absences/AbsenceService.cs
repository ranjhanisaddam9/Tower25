using System.Globalization;
using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.People;
using HR.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Absences;

public enum PaidStatusFilter
{
    All,
    Paid,
    Unpaid,
    PartlyPaid,
}

public sealed record AbsenceQuery(
    DateOnly PeriodStart,
    string? Search,
    AbsencePortion? Portion,
    PaidStatusFilter Paid,
    PersonStatusFilter Status,
    int Page,
    int PageSize = AbsenceService.DefaultPageSize);

/// <summary>A list row; paid and unpaid parts come from the allocator, never from the database.</summary>
public sealed record AbsenceRow(
    int Id,
    int PersonId,
    string PersonCode,
    string PersonName,
    DateOnly Date,
    AbsencePortion Portion,
    decimal PaidDays,
    decimal UnpaidDays,
    string? Note,
    string? AddedBy,
    int LaterChangedIfDeleted);

/// <summary>One person's totals for a period: absent days, paid leave used, unpaid days and payable days.</summary>
public sealed record AbsenceSummary(int PersonId, string PersonCode, string PersonName, decimal AbsentDays, decimal PaidDays, PayableDaysResult Days);

public sealed record AbsenceList(PayPeriod Period, bool Locked, PagedResult<AbsenceRow> Rows, IReadOnlyList<AbsenceSummary> Summary);

public sealed record AbsenceDetails(
    int Id,
    int PersonId,
    string PersonCode,
    string PersonName,
    DateOnly Date,
    AbsencePortion Portion,
    string? Note,
    byte[] RowVersion,
    bool Locked,
    decimal PaidDays,
    decimal UnpaidDays,
    int LaterChangedIfOtherPortion,
    int LaterChangedIfDeleted);

public sealed record AbsencePerson(int Id, string Code, string FullName, bool IsActive, IReadOnlyList<EmploymentSpan> Employment);

// ---------- Daily attendance ----------

public sealed record AttendanceRow(int PersonId, string PersonCode, string PersonName, string Designation, AbsencePortion? Portion, string? Note);

public sealed record AttendanceSheet(DateOnly Date, bool Locked, IReadOnlyList<AttendanceRow> Rows);

/// <summary>One posted row of the daily sheet: no portion means present.</summary>
public sealed record AttendanceEntry(int PersonId, AbsencePortion? Portion, string? Note);

// ---------- Range entry ----------

public enum RangeOutcome
{
    WillAdd,
    Weekend,
    AlreadyRecorded,
    NotEmployed,
    Locked,
    TooFarAhead,
}

public sealed record RangeDay(DateOnly Date, RangeOutcome Outcome, decimal PaidDays, decimal UnpaidDays);

public sealed record RangePreview(AbsencePerson Person, DateOnly From, DateOnly To, IReadOnlyList<RangeDay> Days)
{
    public IEnumerable<RangeDay> ToAdd => Days.Where(d => d.Outcome == RangeOutcome.WillAdd);
}

// ---------- Person tab and dashboard ----------

public sealed record CalendarDay(DateOnly Date, bool InMonth, bool Weekend, bool Employed, bool IsToday, int? AbsenceId, AllocatedAbsence? Absence);

public sealed record PersonAbsenceRow(int Id, DateOnly Date, AbsencePortion Portion, decimal PaidDays, decimal UnpaidDays, string? Note, bool Locked, int LaterChangedIfDeleted);

public sealed record PersonAbsenceTab(
    int PersonId,
    DateOnly Month,
    IReadOnlyList<CalendarDay> Calendar,
    decimal PaidLeaveLeft,
    PayPeriod CurrentPeriod,
    PayableDaysResult CurrentPeriodDays,
    int Year,
    IReadOnlyList<int> Years,
    IReadOnlyList<PersonAbsenceRow> Rows);

public sealed record AbsenceDashboard(decimal PeriodDays, decimal PeriodUnpaidDays, int AbsentToday);

// ---------- Results ----------

public enum AbsenceResultStatus
{
    Success,
    NotFound,
    Invalid,
    Locked,
    Conflict,
}

public sealed record AbsenceError(string Field, string Message);

public sealed record AbsenceResult(
    AbsenceResultStatus Status,
    int? Id = null,
    IReadOnlyList<AbsenceError>? Errors = null,
    int LaterChanged = 0,
    int Added = 0,
    int Changed = 0,
    int Removed = 0)
{
    public bool Succeeded => Status == AbsenceResultStatus.Success;

    public static AbsenceResult Invalid(string field, string message) => new(AbsenceResultStatus.Invalid, Errors: [new AbsenceError(field, message)]);

    public static AbsenceResult LockedResult() => new(AbsenceResultStatus.Locked, Errors: [new AbsenceError(string.Empty, AbsenceRules.LockedMessage)]);
}

/// <summary>
/// Absences and paid leave (SPEC §4). Paid/unpaid is always computed by <see cref="PaidLeaveAllocator"/> from all of a
/// person's absences in the month; nothing about it is stored. Absences in a finalized payroll period are locked.
/// Audit events 14xx carry who, which absence, which person, the date and the portion; never the note.
/// </summary>
public sealed class AbsenceService(AppDbContext db, IClock clock, IPayrollLock payrollLock, ILoggerFactory loggerFactory)
{
    public const int DefaultPageSize = 20;
    public const string ConflictMessage = "Someone else changed this absence while you were editing. Reload it and try again.";
    public const string DayConflictMessage = "Someone else changed attendance for this day at the same time. Reload the day and try again.";
    public const string RangeTooLongMessage = "Pick at most 31 days.";
    public const string RangeOrderMessage = "The end date can't be before the start date.";
    public const string RangeChangedMessage = "Some dates can no longer be added. Review the preview again.";

    public const string PersonField = "PersonId";
    public const string DateField = "Date";
    public const string PortionField = "Portion";
    public const string NoteField = "Note";
    public const string ToField = "To";

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    // ===================== Queries =====================

    public async Task<AbsenceList> ListAsync(AbsenceQuery query, CancellationToken cancellationToken = default)
    {
        var period = PayPeriod.For(query.PeriodStart);
        var (monthStart, monthEnd) = MonthOf(period.Start);

        var people = db.People.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            people = people.Where(p => p.FullName.Contains(term) || p.Code.Contains(term));
        }

        people = query.Status switch
        {
            PersonStatusFilter.Active => people.Where(p => p.IsActive),
            PersonStatusFilter.Inactive => people.Where(p => !p.IsActive),
            _ => people,
        };

        // The whole month: allocation runs across both periods.
        var rows = await (
                from a in db.Absences.AsNoTracking()
                where a.Date >= monthStart && a.Date <= monthEnd
                join p in people on a.PersonId equals p.Id
                join u in db.Users on a.CreatedByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                select new { a.Id, a.PersonId, p.Code, p.FullName, a.Date, a.Portion, a.Note, AddedBy = u == null ? null : u.FullName })
            .ToListAsync(cancellationToken);

        var inPeriod = new List<AbsenceRow>();
        var summaries = new List<(int PersonId, string Code, string Name, decimal Days, decimal Paid, List<AllocatedAbsence> All)>();
        foreach (var person in rows.GroupBy(r => r.PersonId))
        {
            var days = person.Select(r => new AbsenceDay(r.Date, r.Portion)).ToList();
            var allocated = PaidLeaveAllocator.Allocate(days).ToDictionary(a => a.Date);
            var periodRows = person.Where(r => period.Contains(r.Date)).ToList();
            if (periodRows.Count == 0)
            {
                continue;
            }

            foreach (var r in periodRows)
            {
                var a = allocated[r.Date];
                inPeriod.Add(new AbsenceRow(r.Id, r.PersonId, r.Code, r.FullName, r.Date, r.Portion, a.PaidDays, a.UnpaidDays, r.Note, r.AddedBy,
                    LaterChanged(days, days.Where(d => d.Date != r.Date).ToList(), r.Date)));
            }

            var first = periodRows[0];
            summaries.Add((first.PersonId, first.Code, first.FullName,
                periodRows.Sum(r => r.Portion.Days()),
                periodRows.Sum(r => allocated[r.Date].PaidDays),
                [.. allocated.Values]));
        }

        var filtered = inPeriod
            .Where(r => query.Portion is not { } portion || r.Portion == portion)
            .Where(r => query.Paid switch
            {
                PaidStatusFilter.Paid => r.UnpaidDays == 0m,
                PaidStatusFilter.Unpaid => r.PaidDays == 0m,
                PaidStatusFilter.PartlyPaid => r.PaidDays > 0m && r.UnpaidDays > 0m,
                _ => true,
            })
            .OrderBy(r => r.Date)
            .ThenBy(r => r.PersonName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var page = PagedResult<AbsenceRow>.ClampPage(query.Page, filtered.Count, query.PageSize);
        var pageRows = filtered.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToList();

        var employment = await EmploymentAsync(summaries.Select(s => s.PersonId), cancellationToken);
        var summary = summaries
            .Select(s => new AbsenceSummary(s.PersonId, s.Code, s.Name, s.Days, s.Paid,
                PayableDays.For(employment.GetValueOrDefault(s.PersonId, []), s.All, period)))
            .OrderBy(s => s.PersonName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new AbsenceList(period, await payrollLock.IsLockedAsync(period.Start, cancellationToken),
            new PagedResult<AbsenceRow>(pageRows, page, query.PageSize, filtered.Count), summary);
    }

    public async Task<AbsenceDetails?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await (
                from a in db.Absences.AsNoTracking()
                where a.Id == id
                join p in db.People on a.PersonId equals p.Id
                select new { a.Id, a.PersonId, p.Code, p.FullName, a.Date, a.Portion, a.Note, a.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var month = await MonthDaysAsync(row.PersonId, row.Date, cancellationToken);
        var allocated = PaidLeaveAllocator.Allocate(month).Single(a => a.Date == row.Date);
        var other = row.Portion == AbsencePortion.Full ? AbsencePortion.Half : AbsencePortion.Full;
        var switched = month.Select(d => d.Date == row.Date ? d with { Portion = other } : d).ToList();
        var removed = month.Where(d => d.Date != row.Date).ToList();
        return new AbsenceDetails(row.Id, row.PersonId, row.Code, row.FullName, row.Date, row.Portion, row.Note, row.RowVersion,
            await IsLockedAsync(row.Date, cancellationToken), allocated.PaidDays, allocated.UnpaidDays,
            LaterChanged(month, switched, row.Date), LaterChanged(month, removed, row.Date));
    }

    public async Task<AbsencePerson?> GetPersonAsync(int personId, CancellationToken cancellationToken = default)
    {
        var person = await db.People.AsNoTracking()
            .Where(p => p.Id == personId)
            .Select(p => new { p.Id, p.Code, p.FullName, p.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (person is null)
        {
            return null;
        }

        var employment = await EmploymentAsync([personId], cancellationToken);
        return new AbsencePerson(person.Id, person.Code, person.FullName, person.IsActive, employment.GetValueOrDefault(personId, []));
    }

    /// <summary>People for the pickers on the add and range forms: active first, then by name.</summary>
    public async Task<IReadOnlyList<(int Id, string Code, string FullName, bool IsActive)>> PeopleForPickerAsync(CancellationToken cancellationToken = default) =>
        (await db.People.AsNoTracking()
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.FullName)
            .Select(p => new { p.Id, p.Code, p.FullName, p.IsActive })
            .ToListAsync(cancellationToken))
        .Select(p => (p.Id, p.Code, p.FullName, p.IsActive))
        .ToList();

    // ===================== Single add, edit, delete =====================

    public async Task<AbsenceResult> CreateAsync(int personId, DateOnly date, AbsencePortion portion, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await GetPersonAsync(personId, cancellationToken);
        if (person is null)
        {
            return AbsenceResult.Invalid(PersonField, "Choose a person.");
        }

        if (NoteError(note) is { } noteError)
        {
            return noteError;
        }

        if (AbsenceRules.IsWeekday(date) && await IsLockedAsync(date, cancellationToken))
        {
            return AbsenceResult.LockedResult();
        }

        var month = await MonthDaysAsync(personId, date, cancellationToken);
        if (AbsenceRules.DateError(date, clock.Today, person.Employment, month.Any(d => d.Date == date)) is { } error)
        {
            return AbsenceResult.Invalid(DateField, error);
        }

        var absence = Absence.Create(personId, date, portion, note, actorId, clock.UtcNow);
        db.Absences.Add(absence);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.AbsenceCreated(_log, actorId, absence.Id, personId, date, portion.ToString());
        return new AbsenceResult(AbsenceResultStatus.Success, absence.Id, LaterChanged: LaterChanged(month, [.. month, new AbsenceDay(date, portion)], date));
    }

    /// <summary>Changes the portion and note of an absence. The date is fixed: delete and add to move it.</summary>
    public async Task<AbsenceResult> UpdateAsync(int id, AbsencePortion portion, string? note, byte[] rowVersion, string actorId, CancellationToken cancellationToken = default)
    {
        var absence = await db.Absences.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (absence is null)
        {
            return new AbsenceResult(AbsenceResultStatus.NotFound);
        }

        if (await IsLockedAsync(absence.Date, cancellationToken))
        {
            return AbsenceResult.LockedResult();
        }

        if (!absence.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new AbsenceResult(AbsenceResultStatus.Conflict, id, [new AbsenceError(string.Empty, ConflictMessage)]);
        }

        if (NoteError(note) is { } noteError)
        {
            return noteError;
        }

        var month = await MonthDaysAsync(absence.PersonId, absence.Date, cancellationToken);
        var old = absence.Portion;
        db.Entry(absence).Property(a => a.RowVersion).OriginalValue = rowVersion;
        absence.Update(portion, note, actorId, clock.UtcNow);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.AbsenceEdited(_log, actorId, absence.Id, absence.PersonId, absence.Date, old.ToString(), portion.ToString());
        var after = month.Select(d => d.Date == absence.Date ? d with { Portion = portion } : d).ToList();
        return new AbsenceResult(AbsenceResultStatus.Success, absence.Id, LaterChanged: LaterChanged(month, after, absence.Date));
    }

    public async Task<AbsenceResult> DeleteAsync(int id, string actorId, CancellationToken cancellationToken = default)
    {
        var absence = await db.Absences.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (absence is null)
        {
            return new AbsenceResult(AbsenceResultStatus.NotFound);
        }

        if (await IsLockedAsync(absence.Date, cancellationToken))
        {
            return AbsenceResult.LockedResult();
        }

        var month = await MonthDaysAsync(absence.PersonId, absence.Date, cancellationToken);
        db.Absences.Remove(absence);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.AbsenceDeleted(_log, actorId, absence.Id, absence.PersonId, absence.Date, absence.Portion.ToString());
        return new AbsenceResult(AbsenceResultStatus.Success, absence.PersonId,
            LaterChanged: LaterChanged(month, month.Where(d => d.Date != absence.Date).ToList(), absence.Date));
    }

    // ===================== Daily attendance =====================

    /// <summary>Everyone employed on <paramref name="date"/> with their absence that day, if any. Weekends have no sheet.</summary>
    public async Task<AttendanceSheet?> GetDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        if (!AbsenceRules.IsWeekday(date))
        {
            return null;
        }

        var rows = await (
                from p in db.People.AsNoTracking()
                where db.EmploymentPeriods.Any(e => e.PersonId == p.Id && e.StartDate <= date && (e.EndDate == null || e.EndDate >= date))
                join a in db.Absences.Where(a => a.Date == date) on p.Id equals a.PersonId into absences
                from a in absences.DefaultIfEmpty()
                orderby p.FullName
                select new AttendanceRow(p.Id, p.Code, p.FullName, p.Designation, a == null ? null : a.Portion, a == null ? null : a.Note))
            .ToListAsync(cancellationToken);

        return new AttendanceSheet(date, await IsLockedAsync(date, cancellationToken), rows);
    }

    /// <summary>
    /// Saves the daily sheet: adds, changes and removes only the rows that differ, in one transaction. Every posted
    /// person must be employed that day; anything else rejects the whole save.
    /// </summary>
    public async Task<AbsenceResult> SaveDayAsync(DateOnly date, IReadOnlyList<AttendanceEntry> entries, string actorId, CancellationToken cancellationToken = default)
    {
        if (!AbsenceRules.IsWeekday(date))
        {
            return AbsenceResult.Invalid(DateField, AbsenceRules.WeekendMessage);
        }

        if (date > AbsenceRules.LatestAllowed(clock.Today))
        {
            return AbsenceResult.Invalid(DateField, AbsenceRules.TooFarAheadMessage);
        }

        if (await IsLockedAsync(date, cancellationToken))
        {
            return AbsenceResult.LockedResult();
        }

        if (entries.GroupBy(e => e.PersonId).Any(g => g.Count() > 1))
        {
            return AbsenceResult.Invalid(string.Empty, "Each person can appear only once.");
        }

        foreach (var entry in entries)
        {
            if (NoteError(entry.Note) is { } noteError)
            {
                return noteError;
            }
        }

        var ids = entries.Select(e => e.PersonId).ToList();
        var employed = await db.EmploymentPeriods.AsNoTracking()
            .Where(e => ids.Contains(e.PersonId) && e.StartDate <= date && (e.EndDate == null || e.EndDate >= date))
            .Select(e => e.PersonId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (employed.Count != ids.Count)
        {
            return AbsenceResult.Invalid(string.Empty, "Someone on the sheet wasn't employed on that date. Reload the day and try again.");
        }

        var existing = await db.Absences.Where(a => a.Date == date && ids.Contains(a.PersonId)).ToDictionaryAsync(a => a.PersonId, cancellationToken);
        var now = clock.UtcNow;
        var added = new List<Absence>();
        var changed = new List<(Absence Absence, AbsencePortion Old)>();
        var removed = new List<Absence>();
        foreach (var entry in entries)
        {
            existing.TryGetValue(entry.PersonId, out var current);
            if (entry.Portion is not { } portion)
            {
                if (current is not null)
                {
                    db.Absences.Remove(current);
                    removed.Add(current);
                }
            }
            else if (current is null)
            {
                var absence = Absence.Create(entry.PersonId, date, portion, entry.Note, actorId, now);
                db.Absences.Add(absence);
                added.Add(absence);
            }
            else if (current.Portion != portion || current.Note != Absence.NormalizeNote(entry.Note))
            {
                var old = current.Portion;
                current.Update(portion, entry.Note, actorId, now);
                changed.Add((current, old));
            }
        }

        if (added.Count + changed.Count + removed.Count == 0)
        {
            return new AbsenceResult(AbsenceResultStatus.Success);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await SaveAsync(cancellationToken, DayConflictMessage) is { } failure)
        {
            return failure;
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var a in added)
        {
            SecurityLog.AbsenceCreated(_log, actorId, a.Id, a.PersonId, date, a.Portion.ToString());
        }

        foreach (var (a, old) in changed)
        {
            SecurityLog.AbsenceEdited(_log, actorId, a.Id, a.PersonId, date, old.ToString(), a.Portion.ToString());
        }

        foreach (var a in removed)
        {
            SecurityLog.AbsenceDeleted(_log, actorId, a.Id, a.PersonId, date, a.Portion.ToString());
        }

        return new AbsenceResult(AbsenceResultStatus.Success, Added: added.Count, Changed: changed.Count, Removed: removed.Count);
    }

    // ===================== Range entry =====================

    /// <summary>Classifies every date from <paramref name="from"/> to <paramref name="to"/> and allocates the ones that would be added.</summary>
    public async Task<(RangePreview? Preview, AbsenceResult? Error)> PreviewRangeAsync(int personId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            return (null, AbsenceResult.Invalid(ToField, RangeOrderMessage));
        }

        if (to.DayNumber - from.DayNumber + 1 > AbsenceRules.MaxRangeDays)
        {
            return (null, AbsenceResult.Invalid(ToField, RangeTooLongMessage));
        }

        var person = await GetPersonAsync(personId, cancellationToken);
        if (person is null)
        {
            return (null, AbsenceResult.Invalid(PersonField, "Choose a person."));
        }

        // Every month the range touches, for the allocation.
        var (start, _) = MonthOf(from);
        var (_, end) = MonthOf(to);
        var existing = await db.Absences.AsNoTracking()
            .Where(a => a.PersonId == personId && a.Date >= start && a.Date <= end)
            .Select(a => new AbsenceDay(a.Date, a.Portion))
            .ToListAsync(cancellationToken);
        var recorded = existing.Select(d => d.Date).ToHashSet();

        var locks = new Dictionary<DateOnly, bool>();
        var classified = new List<(DateOnly Date, RangeOutcome Outcome)>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var period = PayPeriod.For(date).Start;
            if (!locks.TryGetValue(period, out var locked))
            {
                locked = await payrollLock.IsLockedAsync(period, cancellationToken);
                locks[period] = locked;
            }

            classified.Add((date, Classify(date, clock.Today, person.Employment, recorded.Contains(date), locked)));
        }

        var allocated = PaidLeaveAllocator.Allocate(existing.Concat(classified
                .Where(c => c.Outcome == RangeOutcome.WillAdd)
                .Select(c => new AbsenceDay(c.Date, AbsencePortion.Full))))
            .ToDictionary(a => a.Date);
        var days = classified
            .Select(c => c.Outcome == RangeOutcome.WillAdd
                ? new RangeDay(c.Date, c.Outcome, allocated[c.Date].PaidDays, allocated[c.Date].UnpaidDays)
                : new RangeDay(c.Date, c.Outcome, 0m, 0m))
            .ToList();
        return (new RangePreview(person, from, to, days), null);
    }

    /// <summary>
    /// Saves the posted dates as full-day absences in one transaction. Re-validates everything: the preview is never
    /// trusted, and any posted date that can't be added (or lies outside the range) rejects the whole confirm.
    /// </summary>
    public async Task<AbsenceResult> ConfirmRangeAsync(int personId, DateOnly from, DateOnly to, IReadOnlyCollection<DateOnly> dates, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        if (NoteError(note) is { } noteError)
        {
            return noteError;
        }

        var (preview, error) = await PreviewRangeAsync(personId, from, to, cancellationToken);
        if (preview is null)
        {
            return error!;
        }

        var allowed = preview.ToAdd.Select(d => d.Date).ToHashSet();
        if (dates.Count == 0 || dates.Distinct().Count() != dates.Count || dates.Any(d => !allowed.Contains(d)))
        {
            return AbsenceResult.Invalid(string.Empty, RangeChangedMessage);
        }

        var now = clock.UtcNow;
        var added = dates.Order().Select(d => Absence.Create(personId, d, AbsencePortion.Full, note, actorId, now)).ToList();
        db.Absences.AddRange(added);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await SaveAsync(cancellationToken, RangeChangedMessage) is { } failure)
        {
            return failure;
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var a in added)
        {
            SecurityLog.AbsenceCreated(_log, actorId, a.Id, personId, a.Date, a.Portion.ToString());
        }

        return new AbsenceResult(AbsenceResultStatus.Success, personId, Added: added.Count);
    }

    // ===================== Person tab and dashboard =====================

    public async Task<PersonAbsenceTab?> GetPersonTabAsync(int personId, DateOnly? month, int? year, CancellationToken cancellationToken = default)
    {
        var person = await GetPersonAsync(personId, cancellationToken);
        if (person is null)
        {
            return null;
        }

        var today = clock.Today;
        var shown = month is { } m ? new DateOnly(m.Year, m.Month, 1) : new DateOnly(today.Year, today.Month, 1);
        var all = await db.Absences.AsNoTracking()
            .Where(a => a.PersonId == personId)
            .OrderBy(a => a.Date)
            .Select(a => new { a.Id, a.Date, a.Portion, a.Note })
            .ToListAsync(cancellationToken);
        var allocated = PaidLeaveAllocator.Allocate(all.Select(a => new AbsenceDay(a.Date, a.Portion))).ToDictionary(a => a.Date);
        var ids = all.ToDictionary(a => a.Date, a => a.Id);

        // A Monday-first grid covering the whole month.
        var (monthStart, monthEnd) = MonthOf(shown);
        var gridStart = monthStart.AddDays(-(((int)monthStart.DayOfWeek + 6) % 7));
        var gridEnd = monthEnd.AddDays(6 - (((int)monthEnd.DayOfWeek + 6) % 7));
        var calendar = new List<CalendarDay>();
        for (var d = gridStart; d <= gridEnd; d = d.AddDays(1))
        {
            calendar.Add(new CalendarDay(d, d.Month == shown.Month, !AbsenceRules.IsWeekday(d), EmploymentCalendar.IsEmployedOn(person.Employment, d), d == today,
                ids.TryGetValue(d, out var id) ? id : null, allocated.TryGetValue(d, out var a) ? a : null));
        }

        var period = PayPeriod.For(today);
        var shownYear = year is { } y and >= 2000 and <= 2100 ? y : today.Year;
        var years = all.Select(a => a.Date.Year).Append(today.Year).Append(shownYear).Distinct().OrderDescending().ToList();
        var lockCache = new Dictionary<DateOnly, bool>();
        var rows = new List<PersonAbsenceRow>();
        foreach (var a in all.Where(a => a.Date.Year == shownYear).OrderByDescending(a => a.Date))
        {
            var start = PayPeriod.For(a.Date).Start;
            if (!lockCache.TryGetValue(start, out var locked))
            {
                locked = await payrollLock.IsLockedAsync(start, cancellationToken);
                lockCache[start] = locked;
            }

            var split = allocated[a.Date];
            var monthDays = all.Where(x => x.Date.Year == a.Date.Year && x.Date.Month == a.Date.Month).Select(x => new AbsenceDay(x.Date, x.Portion)).ToList();
            rows.Add(new PersonAbsenceRow(a.Id, a.Date, a.Portion, split.PaidDays, split.UnpaidDays, a.Note, locked,
                LaterChanged(monthDays, monthDays.Where(x => x.Date != a.Date).ToList(), a.Date)));
        }

        return new PersonAbsenceTab(
            personId,
            shown,
            calendar,
            PaidLeaveAllocator.LeftInMonth(allocated.Values.Select(a => new AbsenceDay(a.Date, a.Portion)), shown),
            period,
            PayableDays.For(person.Employment, allocated.Values, period),
            shownYear,
            years,
            rows);
    }

    /// <summary>Dashboard tiles: absence days and unpaid days in the current period (all people), and people absent today.</summary>
    public async Task<AbsenceDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var period = PayPeriod.For(today);
        var (monthStart, monthEnd) = MonthOf(period.Start);
        var month = await db.Absences.AsNoTracking()
            .Where(a => a.Date >= monthStart && a.Date <= monthEnd)
            .Select(a => new { a.PersonId, a.Date, a.Portion })
            .ToListAsync(cancellationToken);

        var allocated = month
            .GroupBy(a => a.PersonId)
            .SelectMany(g => PaidLeaveAllocator.Allocate(g.Select(a => new AbsenceDay(a.Date, a.Portion))))
            .Where(a => period.Contains(a.Date))
            .ToList();
        return new AbsenceDashboard(allocated.Sum(a => a.Days), allocated.Sum(a => a.UnpaidDays), month.Count(a => a.Date == today));
    }

    // ===================== Helpers =====================

    public static string MonthLabel(DateOnly date) => date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private static RangeOutcome Classify(DateOnly date, DateOnly today, IReadOnlyList<EmploymentSpan> employment, bool recorded, bool locked)
    {
        if (!AbsenceRules.IsWeekday(date))
        {
            return RangeOutcome.Weekend;
        }

        if (locked)
        {
            return RangeOutcome.Locked;
        }

        return AbsenceRules.DateError(date, today, employment, recorded) switch
        {
            null => RangeOutcome.WillAdd,
            AbsenceRules.DuplicateMessage => RangeOutcome.AlreadyRecorded,
            AbsenceRules.NotEmployedMessage => RangeOutcome.NotEmployed,
            _ => RangeOutcome.TooFarAhead,
        };
    }

    /// <summary>How many absences after <paramref name="date"/> in its month change paid/unpaid between two versions of the month.</summary>
    private static int LaterChanged(IReadOnlyList<AbsenceDay> before, IReadOnlyList<AbsenceDay> after, DateOnly date)
    {
        var old = PaidLeaveAllocator.Allocate(before).ToDictionary(a => a.Date);
        return PaidLeaveAllocator.Allocate(after)
            .Count(a => a.Date > date && a.Date.Month == date.Month && a.Date.Year == date.Year
                && old.TryGetValue(a.Date, out var o) && (o.PaidDays, o.UnpaidDays) != (a.PaidDays, a.UnpaidDays));
    }

    private static (DateOnly Start, DateOnly End) MonthOf(DateOnly date) =>
        (new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)));

    private static AbsenceResult? NoteError(string? note) =>
        note is not null && note.Trim().Length > Absence.NoteMaxLength ? AbsenceResult.Invalid(NoteField, AbsenceRules.NoteTooLongMessage) : null;

    private async Task<List<AbsenceDay>> MonthDaysAsync(int personId, DateOnly date, CancellationToken cancellationToken)
    {
        var (start, end) = MonthOf(date);
        return await db.Absences.AsNoTracking()
            .Where(a => a.PersonId == personId && a.Date >= start && a.Date <= end)
            .Select(a => new AbsenceDay(a.Date, a.Portion))
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<int, IReadOnlyList<EmploymentSpan>>> EmploymentAsync(IEnumerable<int> personIds, CancellationToken cancellationToken)
    {
        var ids = personIds.Distinct().ToList();
        var spans = await db.EmploymentPeriods.AsNoTracking()
            .Where(e => ids.Contains(e.PersonId))
            .OrderBy(e => e.StartDate)
            .Select(e => new { e.PersonId, e.StartDate, e.EndDate })
            .ToListAsync(cancellationToken);
        return spans
            .GroupBy(s => s.PersonId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EmploymentSpan>)g.Select(s => new EmploymentSpan(s.StartDate, s.EndDate)).ToList());
    }

    private Task<bool> IsLockedAsync(DateOnly date, CancellationToken cancellationToken) =>
        payrollLock.IsLockedAsync(PayPeriod.For(date).Start, cancellationToken);

    private async Task<AbsenceResult?> SaveAsync(CancellationToken cancellationToken, string conflictMessage = ConflictMessage)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new AbsenceResult(AbsenceResultStatus.Conflict, Errors: [new AbsenceError(string.Empty, conflictMessage)]);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql
            && sql.Message.Contains(AbsenceConfiguration.DateIndex, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return conflictMessage == ConflictMessage
                ? AbsenceResult.Invalid(DateField, AbsenceRules.DuplicateMessage)
                : new AbsenceResult(AbsenceResultStatus.Conflict, Errors: [new AbsenceError(string.Empty, conflictMessage)]);
        }
    }
}
