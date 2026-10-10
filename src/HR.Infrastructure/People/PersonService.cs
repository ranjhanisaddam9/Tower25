using System.Globalization;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.People;

public enum PersonStatusFilter
{
    Active,
    Inactive,
    All,
}

public enum PersonSort
{
    Name,
    Code,
    Joined,
}

/// <summary>Admin-only list filter. Managers never get this type.</summary>
public enum HireSourceFilter
{
    All,
    NotAssigned,
    CompanyRecommended,
    BudgetHire,
    Owner,
}

public sealed record PeopleQuery(
    string? Search,
    PersonType? Type,
    PersonStatusFilter Status,
    PersonSort Sort,
    bool Descending,
    int Page,
    int PageSize = PersonService.DefaultPageSize);

/// <summary>A list row. Deliberately has no hire source: this is what Managers get.</summary>
public sealed record PersonRow(int Id, string Code, string FullName, string Designation, PersonType Type, DateOnly JoiningDate, DateOnly? LeavingDate, bool IsActive);

/// <summary>An Admin list row: the shared row plus the Admin-only hire source.</summary>
public sealed record AdminPersonRow(PersonRow Person, HireSource? HireSource);

/// <summary>
/// A people-export row (M9): the list row plus contact details. CNIC and IBAN are masked here, in the service, so the
/// full values never leave it for an export. No hire source: this is what Managers get.
/// </summary>
public sealed record PersonExportRow(
    int Id,
    string Code,
    string FullName,
    PersonType Type,
    string Designation,
    string? Email,
    string Phone,
    string? CnicMasked,
    string? BankName,
    string? IbanMasked,
    DateOnly JoiningDate,
    DateOnly? LeavingDate,
    bool IsActive);

/// <summary>Admin people-export row: the shared row plus the Admin-only hire source.</summary>
public sealed record AdminPersonExportRow(PersonExportRow Person, HireSource? HireSource);

/// <summary>Everything on the details and edit pages, without the hire source (loaded separately for Admins).</summary>
public sealed record PersonDetails(
    int Id,
    string Code,
    string FullName,
    PersonType Type,
    string Designation,
    string? Email,
    string Phone,
    string? Cnic,
    string? BankName,
    string? Iban,
    DateOnly JoiningDate,
    DateOnly? LeavingDate,
    bool IsActive,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    byte[] RowVersion);

public sealed record PeopleCounts(int Active, int Employees, int Internees);

public enum PersonResultStatus
{
    Success,
    NotFound,
    Invalid,
    Conflict,
}

/// <summary>Outcome of a people command. On a concurrency conflict, <see cref="Current"/> holds the latest saved values.</summary>
public sealed record PersonResult(PersonResultStatus Status, int? Id = null, IReadOnlyList<PersonError>? Errors = null, PersonDetails? Current = null)
{
    public bool Succeeded => Status == PersonResultStatus.Success;

    public static PersonResult Invalid(string field, string message) => new(PersonResultStatus.Invalid, Errors: [new PersonError(field, message)]);
}

/// <summary>
/// People commands and queries (SPEC §2). Manager-facing queries project rows without the hire source, so it is never
/// even loaded for a Manager; the hire source has its own Admin-only methods.
/// Never logs CNIC, IBAN or phone numbers.
/// </summary>
public sealed class PersonService(AppDbContext db, IClock clock, IPayrollLock payrollLock, ILoggerFactory loggerFactory)
{
    public const int DefaultPageSize = 20;

    public const string DuplicateEmailMessage = "Another person already has this email.";
    public const string DuplicateCnicMessage = "Another person already has this CNIC.";
    public const string RejoiningDateField = "RejoiningDate";
    public const string LeavingDateField = "LeavingDate";

    public const string HireSourceLockedMessage = "Delete this person's pay records before changing the hire source.";

    /// <summary>The error number THROWn by the TR_People_SingleActiveOwner trigger.</summary>
    public const int ActiveOwnerErrorNumber = 51002;

    /// <summary>The error number THROWn by the TR_EmploymentPeriods_NoOverlap trigger.</summary>
    public const int OverlapErrorNumber = 51001;

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    // ---------- Queries ----------

    public async Task<PagedResult<PersonRow>> ListAsync(PeopleQuery query, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var filtered = Filter(db.People.AsNoTracking(), query, today);
        var total = await filtered.CountAsync(cancellationToken);
        var page = PagedResult<PersonRow>.ClampPage(query.Page, total, query.PageSize);

        var rows = await Sort(filtered, query)
            .Skip((page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new PersonRow(p.Id, p.Code, p.FullName, p.Designation, p.Type, p.JoiningDate, p.LeavingDate, p.LeavingDate == null || p.LeavingDate >= today))
            .ToListAsync(cancellationToken);

        return new PagedResult<PersonRow>(rows, page, query.PageSize, total);
    }

    /// <summary>Admin only: the list with hire sources and the hire-source filter.</summary>
    public async Task<PagedResult<AdminPersonRow>> ListForAdminAsync(PeopleQuery query, HireSourceFilter hireSource, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var filtered = FilterHireSource(Filter(db.People.AsNoTracking(), query, today), hireSource);

        var total = await filtered.CountAsync(cancellationToken);
        var page = PagedResult<AdminPersonRow>.ClampPage(query.Page, total, query.PageSize);

        var rows = await Sort(filtered, query)
            .Skip((page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(p => new AdminPersonRow(
                new PersonRow(p.Id, p.Code, p.FullName, p.Designation, p.Type, p.JoiningDate, p.LeavingDate, p.LeavingDate == null || p.LeavingDate >= today),
                p.HireSource))
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminPersonRow>(rows, page, query.PageSize, total);
    }

    /// <summary>Every row of the list with the same filters and sort (no paging), for the people export.</summary>
    public async Task<IReadOnlyList<PersonExportRow>> ExportAsync(PeopleQuery query, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var rows = await Sort(Filter(db.People.AsNoTracking(), query, today), query)
            .Select(p => new { p.Id, p.Code, p.FullName, p.Type, p.Designation, p.Email, p.Phone, p.Cnic, p.BankName, p.Iban, p.JoiningDate, p.LeavingDate })
            .ToListAsync(cancellationToken);
        return rows.Select(p => new PersonExportRow(p.Id, p.Code, p.FullName, p.Type, p.Designation, p.Email, p.Phone,
                p.Cnic is null ? null : Masking.Cnic(p.Cnic), p.BankName, p.Iban is null ? null : Masking.Iban(p.Iban),
                p.JoiningDate, p.LeavingDate, PersonStatus.IsActive(p.LeavingDate, today)))
            .ToList();
    }

    /// <summary>Admin only: the people export with hire sources and the hire-source filter.</summary>
    public async Task<IReadOnlyList<AdminPersonExportRow>> ExportForAdminAsync(PeopleQuery query, HireSourceFilter hireSource, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var rows = await Sort(FilterHireSource(Filter(db.People.AsNoTracking(), query, today), hireSource), query)
            .Select(p => new { p.Id, p.Code, p.FullName, p.Type, p.Designation, p.Email, p.Phone, p.Cnic, p.BankName, p.Iban, p.JoiningDate, p.LeavingDate, p.HireSource })
            .ToListAsync(cancellationToken);
        return rows.Select(p => new AdminPersonExportRow(
                new PersonExportRow(p.Id, p.Code, p.FullName, p.Type, p.Designation, p.Email, p.Phone,
                    p.Cnic is null ? null : Masking.Cnic(p.Cnic), p.BankName, p.Iban is null ? null : Masking.Iban(p.Iban),
                    p.JoiningDate, p.LeavingDate, PersonStatus.IsActive(p.LeavingDate, today)),
                p.HireSource))
            .ToList();
    }

    /// <summary>Every employment period of a person, oldest first.</summary>
    public async Task<IReadOnlyList<EmploymentSpan>> GetEmploymentHistoryAsync(int id, CancellationToken cancellationToken = default) =>
        await db.EmploymentPeriods.AsNoTracking()
            .Where(p => p.PersonId == id)
            .OrderBy(p => p.StartDate)
            .Select(p => new EmploymentSpan(p.StartDate, p.EndDate))
            .ToListAsync(cancellationToken);

    public Task<PersonDetails?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        db.People.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PersonDetails(
                p.Id, p.Code, p.FullName, p.Type, p.Designation, p.Email, p.Phone, p.Cnic, p.BankName, p.Iban,
                p.JoiningDate, p.LeavingDate, p.LeavingDate == null || p.LeavingDate >= clock.Today, p.Notes, p.CreatedAt, p.UpdatedAt, p.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>Admin only. Returns null when the person does not exist; a found person may have a null source.</summary>
    public async Task<(bool Found, HireSource? Source)> GetHireSourceAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await db.People.AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.HireSource })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? (false, null) : (true, row.HireSource);
    }

    public async Task<PeopleCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        var byType = await db.People.AsNoTracking()
            .Where(PersonStatus.ActiveOn(clock.Today))
            .GroupBy(p => p.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var employees = byType.Where(x => x.Type == PersonType.Employee).Sum(x => x.Count);
        var internees = byType.Where(x => x.Type == PersonType.Internee).Sum(x => x.Count);
        return new PeopleCounts(employees + internees, employees, internees);
    }

    /// <summary>Admin only: active people whose hire source has not been assigned yet.</summary>
    public Task<int> CountActiveWithoutHireSourceAsync(CancellationToken cancellationToken = default) =>
        db.People.AsNoTracking().Where(PersonStatus.ActiveOn(clock.Today)).CountAsync(p => p.HireSource == null, cancellationToken);

    // ---------- Commands ----------

    /// <param name="confirmLateAddition">The user confirmed the person is not in the finalized payrolls their joining date covers.</param>
    public async Task<PersonResult> CreateAsync(PersonInput input, string actorId, bool confirmLateAddition = false, CancellationToken cancellationToken = default)
    {
        var normalized = input.Normalize(out var errors);
        if (normalized is null)
        {
            return new PersonResult(PersonResultStatus.Invalid, Errors: errors);
        }

        var duplicates = await DuplicateErrorsAsync(normalized, exceptId: null, cancellationToken);
        if (duplicates.Count > 0)
        {
            return new PersonResult(PersonResultStatus.Invalid, Errors: duplicates);
        }

        var (createRefusal, createUncovered) = await EmploymentImpactAsync(null, normalized.JoiningDate!.Value, null, adds: true, cancellationToken);
        if (LateAdditionCheck(createRefusal, createUncovered, confirmLateAddition, nameof(PersonInput.JoiningDate)) is { } lateCreate)
        {
            return lateCreate;
        }

        // Taken before the insert: if the insert fails the number is simply skipped, never reused.
        var codeNumber = await db.NextPersonCodeNumberAsync(cancellationToken);
        var person = Person.Create(codeNumber, normalized, actorId, clock.UtcNow);
        db.People.Add(person);

        var failure = await SaveAsync(cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        SecurityLog.PersonCreated(_log, actorId, person.Id, person.Code);
        AuditLateAddition(actorId, person, createUncovered);
        return new PersonResult(PersonResultStatus.Success, person.Id);
    }

    /// <summary>
    /// Updates the editable details. When <paramref name="keepCnic"/> or <paramref name="keepIban"/> is set, the stored value
    /// stays (the edit form never shows them in full). <paramref name="rowVersion"/> is the version the form was loaded with.
    /// </summary>
    public async Task<PersonResult> UpdateAsync(
        int id,
        PersonInput input,
        bool keepCnic,
        bool keepIban,
        byte[] rowVersion,
        string actorId,
        bool confirmLateAddition = false,
        CancellationToken cancellationToken = default)
    {
        var person = await db.People.Include(p => p.EmploymentPeriods).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
        {
            return new PersonResult(PersonResultStatus.NotFound);
        }

        if (!person.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return await ConflictAsync(id, cancellationToken);
        }

        input = input with
        {
            Cnic = keepCnic ? person.Cnic : input.Cnic,
            Iban = keepIban ? person.Iban : input.Iban,
        };

        var normalized = input.Normalize(out var errors);
        if (normalized is null)
        {
            return new PersonResult(PersonResultStatus.Invalid, Errors: errors);
        }

        if (person.LeavingDate is { } leaving && normalized.JoiningDate > leaving)
        {
            return PersonResult.Invalid(nameof(PersonInput.JoiningDate), "The joining date can't be after the leaving date.");
        }

        // The joining date is the latest period's start: it must stay after the previous period's end.
        var previousEnd = person.EmploymentPeriods
            .OrderByDescending(p => p.StartDate)
            .Skip(1)
            .Select(p => p.EndDate)
            .FirstOrDefault();
        if (previousEnd is { } ended && normalized.JoiningDate <= ended)
        {
            return PersonResult.Invalid(nameof(PersonInput.JoiningDate),
                $"The joining date must be after the previous employment period, which ended on {ended.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        var duplicates = await DuplicateErrorsAsync(normalized, exceptId: id, cancellationToken);
        if (duplicates.Count > 0)
        {
            return new PersonResult(PersonResultStatus.Invalid, Errors: duplicates);
        }

        // Moving the joining date adds (earlier) or removes (later) the days between the old and new dates.
        List<PayPeriod> updateUncovered = [];
        if (normalized.JoiningDate is { } newJoining && newJoining != person.JoiningDate)
        {
            var adds = newJoining < person.JoiningDate;
            var earlier = adds ? newJoining : person.JoiningDate;
            var later = adds ? person.JoiningDate : newJoining;
            var (refusal, uncovered) = await EmploymentImpactAsync(id, earlier, later.AddDays(-1), adds, cancellationToken);
            if (LateAdditionCheck(refusal, uncovered, confirmLateAddition, nameof(PersonInput.JoiningDate)) is { } late)
            {
                return late;
            }

            updateUncovered = uncovered;
        }

        // The version the user saw is the one EF checks in the UPDATE's WHERE clause.
        db.Entry(person).Property(p => p.RowVersion).OriginalValue = rowVersion;
        person.UpdateDetails(normalized, actorId, clock.UtcNow);

        var failure = await SaveAsync(cancellationToken, conflictId: id);
        if (failure is not null)
        {
            return failure;
        }

        SecurityLog.PersonEdited(_log, actorId, person.Id, person.Code);
        AuditLateAddition(actorId, person, updateUncovered);
        return new PersonResult(PersonResultStatus.Success, person.Id);
    }

    public async Task<PersonResult> DeactivateAsync(int id, DateOnly? leavingDate, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await db.People.Include(p => p.EmploymentPeriods).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
        {
            return new PersonResult(PersonResultStatus.NotFound);
        }

        if (person.LeavingDate is not null)
        {
            return PersonResult.Invalid(LeavingDateField, "This person already has a leaving date.");
        }

        if (leavingDate is null)
        {
            return PersonResult.Invalid(LeavingDateField, "Enter the leaving date.");
        }

        if (leavingDate < person.JoiningDate)
        {
            return PersonResult.Invalid(LeavingDateField, "The leaving date can't be before the joining date.");
        }

        // Employment after the leaving date disappears: no finalized period may lose days.
        if ((await EmploymentImpactAsync(id, leavingDate.Value.AddDays(1), null, adds: false, cancellationToken)).Refusal is { } locked)
        {
            return PersonResult.Invalid(LeavingDateField, locked);
        }

        person.Deactivate(leavingDate.Value, actorId, clock.UtcNow);
        var failure = await SaveAsync(cancellationToken, conflictId: id);
        if (failure is not null)
        {
            return failure;
        }

        SecurityLog.PersonDeactivated(_log, actorId, person.Id, person.Code, leavingDate.Value);
        return new PersonResult(PersonResultStatus.Success, person.Id);
    }

    /// <param name="revealHireSource">True for Admins; a Manager gets a message that does not mention the hire source.</param>
    public async Task<PersonResult> ReactivateAsync(
        int id,
        DateOnly? rejoiningDate,
        string actorId,
        bool revealHireSource,
        bool confirmLateAddition = false,
        CancellationToken cancellationToken = default)
    {
        var person = await db.People.Include(p => p.EmploymentPeriods).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
        {
            return new PersonResult(PersonResultStatus.NotFound);
        }

        if (person.LeavingDate is null)
        {
            return PersonResult.Invalid(RejoiningDateField, "This person has no leaving date, so there is nothing to rejoin from.");
        }

        if (rejoiningDate is null)
        {
            return PersonResult.Invalid(RejoiningDateField, "Enter the rejoining date.");
        }

        if (person.LeavingDate is { } leaving && rejoiningDate <= leaving)
        {
            return PersonResult.Invalid(RejoiningDateField, "The rejoining date must be after the previous leaving date.");
        }

        var (rejoinRefusal, rejoinUncovered) = await EmploymentImpactAsync(id, rejoiningDate.Value, null, adds: true, cancellationToken);
        if (LateAdditionCheck(rejoinRefusal, rejoinUncovered, confirmLateAddition, RejoiningDateField) is { } lateRejoin)
        {
            return lateRejoin;
        }

        var today = clock.Today;
        if (person.HireSource == HireSource.Owner
            && await db.People.Where(PersonStatus.ActiveOn(today)).AnyAsync(p => p.Id != id && p.HireSource == HireSource.Owner, cancellationToken))
        {
            return PersonResult.Invalid(RejoiningDateField, revealHireSource
                ? "Another active person already has the Owner hire source. Change one of them first."
                : "This person can't be reactivated right now. Please ask the administrator.");
        }

        var (previousJoining, previousLeaving) = person.Reactivate(rejoiningDate.Value, actorId, clock.UtcNow);
        var failure = await SaveAsync(cancellationToken, conflictId: id, ownerMessage: revealHireSource
            ? "Another active person already has the Owner hire source."
            : "This person can't be reactivated right now. Please ask the administrator.", ownerField: RejoiningDateField);
        if (failure is not null)
        {
            return failure;
        }

        SecurityLog.PersonReactivated(_log, actorId, person.Id, person.Code, rejoiningDate.Value, previousJoining, previousLeaving);
        AuditLateAddition(actorId, person, rejoinUncovered);
        return new PersonResult(PersonResultStatus.Success, person.Id);
    }

    /// <summary>
    /// Cancels a leaving date that hasn't passed yet (today counts as not passed). The same employment period is reopened;
    /// no new period is created. Refused when a finalized payroll covers any day after the cancelled date.
    /// </summary>
    public async Task<PersonResult> CancelLeavingAsync(int id, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await db.People.Include(p => p.EmploymentPeriods).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
        {
            return new PersonResult(PersonResultStatus.NotFound);
        }

        if (person.LeavingDate is not { } leaving)
        {
            return PersonResult.Invalid(LeavingDateField, "This person has no leaving date to cancel.");
        }

        if (leaving < clock.Today)
        {
            return PersonResult.Invalid(LeavingDateField, "The leaving date has already passed. Use Reactivate to record a rejoining date.");
        }

        // Cancelling only ever affects someone already employed up to the leaving date, so a finalized period after it is
        // refused either way (there is no confirmation route here).
        var (cancelRefusal, cancelUncovered) = await EmploymentImpactAsync(id, leaving.AddDays(1), null, adds: true, cancellationToken);
        if ((cancelRefusal ?? (cancelUncovered.Count > 0 ? LateAdditionMessage(cancelUncovered) : null)) is { } locked)
        {
            return PersonResult.Invalid(LeavingDateField, locked);
        }

        person.CancelLeaving(clock.Today, actorId, clock.UtcNow);
        var failure = await SaveAsync(cancellationToken, conflictId: id);
        if (failure is not null)
        {
            return failure;
        }

        SecurityLog.PersonLeavingCancelled(_log, actorId, person.Id, person.Code, leaving);
        return new PersonResult(PersonResultStatus.Success, person.Id);
    }

    public const string ConfirmLateAdditionField = "ConfirmLateAddition";

    /// <summary>
    /// What changing a person's employment from <paramref name="from"/> to <paramref name="to"/> (inclusive; null = no end)
    /// means for finalized payrolls (M8):
    /// <list type="bullet">
    /// <item>a finalized period whose payroll includes the person (they have a line) must not change: <c>Refusal</c>;</item>
    /// <item>a finalized period that doesn't include them gains coverage when <paramref name="adds"/>: listed in
    /// <c>Uncovered</c> and allowed only with the late-addition confirmation;</item>
    /// <item>removing days from a finalized period they were never paid in changes nothing paid: allowed.</item>
    /// </list>
    /// </summary>
    private async Task<(string? Refusal, List<PayPeriod> Uncovered)> EmploymentImpactAsync(
        int? personId, DateOnly from, DateOnly? to, bool adds, CancellationToken cancellationToken)
    {
        var uncovered = new List<PayPeriod>();
        if (to is { } end && end < from || await payrollLock.LatestLockedPeriodStartAsync(cancellationToken) is not { } latest)
        {
            return (null, uncovered);
        }

        var last = to is { } stop && stop < latest ? stop : latest;
        for (var period = PayPeriod.For(from); period.Start <= last; period = period.Next())
        {
            if (!await payrollLock.IsLockedAsync(period.Start, cancellationToken))
            {
                continue;
            }

            if (personId is { } id && await payrollLock.IsLockedForPersonAsync(id, period.Start, cancellationToken))
            {
                return (LockedEmploymentMessage(period), uncovered);
            }

            if (adds)
            {
                uncovered.Add(period);
            }
        }

        return (null, uncovered);
    }

    public static string LockedEmploymentMessage(PayPeriod period) =>
        $"This change would alter the finalized payroll for {period.Start.ToString("dd MMM", CultureInfo.InvariantCulture)}–{period.End.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}. " +
        "An administrator has to reopen that payroll first.";

    public static string PeriodsText(IEnumerable<PayPeriod> periods) =>
        string.Join(", ", periods.Select(p => $"{p.Start.ToString("dd", CultureInfo.InvariantCulture)}–{p.End.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}"));

    /// <summary>The confirmation text for employment that covers finalized payrolls the person is not in.</summary>
    public static string LateAdditionMessage(IEnumerable<PayPeriod> periods) =>
        $"This person is not included in the finalized payroll(s) for {PeriodsText(periods)}. Pay any arrears as a Bonus or extra days in the current payroll.";

    /// <summary>Refusal, or the confirmation still needed, for a change; null when it may go ahead.</summary>
    private static PersonResult? LateAdditionCheck(string? refusal, List<PayPeriod> uncovered, bool confirmed, string refusalField) =>
        refusal is not null ? PersonResult.Invalid(refusalField, refusal)
        : uncovered.Count > 0 && !confirmed ? PersonResult.Invalid(ConfirmLateAdditionField, LateAdditionMessage(uncovered))
        : null;

    private void AuditLateAddition(string actorId, Person person, List<PayPeriod> uncovered)
    {
        if (uncovered.Count > 0)
        {
            SecurityLog.PersonLateAdditionConfirmed(_log, actorId, person.Id, person.Code, PeriodsText(uncovered));
        }
    }

    /// <summary>Admin only. At most one active person may have the Owner source (SPEC §2).</summary>
    public async Task<PersonResult> SetHireSourceAsync(int id, HireSource? source, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await db.People.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (person is null)
        {
            return new PersonResult(PersonResultStatus.NotFound);
        }

        // Pay records were derived from the current source (SPEC §2), so the source is locked while any exist.
        if (person.HireSource != source && await db.RateRecords.AnyAsync(r => r.PersonId == id, cancellationToken))
        {
            return PersonResult.Invalid(nameof(Person.HireSource), HireSourceLockedMessage);
        }

        var today = clock.Today;
        if (source == HireSource.Owner && person.IsActiveOn(today))
        {
            var currentOwner = await db.People.AsNoTracking()
                .Where(PersonStatus.ActiveOn(today))
                .Where(p => p.Id != id && p.HireSource == HireSource.Owner)
                .Select(p => new { p.FullName, p.Code })
                .FirstOrDefaultAsync(cancellationToken);
            if (currentOwner is not null)
            {
                return PersonResult.Invalid(nameof(Person.HireSource),
                    $"{currentOwner.FullName} ({currentOwner.Code}) is already the active Owner. Only one active person can have the Owner hire source.");
            }
        }

        var previous = person.HireSource;
        if (previous == source)
        {
            return new PersonResult(PersonResultStatus.Success, person.Id);
        }

        person.SetHireSource(source, actorId, clock.UtcNow);
        var failure = await SaveAsync(cancellationToken, conflictId: id,
            ownerMessage: "Another active person already has the Owner hire source.", ownerField: nameof(Person.HireSource));
        if (failure is not null)
        {
            return failure;
        }

        SecurityLog.PersonHireSourceChanged(_log, actorId, person.Id, person.Code, previous?.ToString() ?? "none", source?.ToString() ?? "none");
        return new PersonResult(PersonResultStatus.Success, person.Id);
    }

    // ---------- Helpers ----------

    private static IQueryable<Person> Filter(IQueryable<Person> people, PeopleQuery query, DateOnly today)
    {
        people = query.Status switch
        {
            PersonStatusFilter.Active => people.Where(PersonStatus.ActiveOn(today)),
            PersonStatusFilter.Inactive => people.Where(PersonStatus.InactiveOn(today)),
            _ => people,
        };

        if (query.Type is { } type)
        {
            people = people.Where(p => p.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var lower = term.ToLowerInvariant();
            // Phone numbers are stored as +92...; match on the digits without a leading 0 (0300-123 → 300123).
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');

            people = digits.Length >= 4
                ? people.Where(p => p.FullName.Contains(term) || p.Code.Contains(term) || (p.Email != null && p.Email.Contains(lower)) || p.Phone.Contains(digits))
                : people.Where(p => p.FullName.Contains(term) || p.Code.Contains(term) || (p.Email != null && p.Email.Contains(lower)));
        }

        return people;
    }

    private static IQueryable<Person> FilterHireSource(IQueryable<Person> people, HireSourceFilter hireSource) => hireSource switch
    {
        HireSourceFilter.NotAssigned => people.Where(p => p.HireSource == null),
        HireSourceFilter.CompanyRecommended => people.Where(p => p.HireSource == HireSource.CompanyRecommended),
        HireSourceFilter.BudgetHire => people.Where(p => p.HireSource == HireSource.BudgetHire),
        HireSourceFilter.Owner => people.Where(p => p.HireSource == HireSource.Owner),
        _ => people,
    };

    private static IQueryable<Person> Sort(IQueryable<Person> people, PeopleQuery query) => (query.Sort, query.Descending) switch
    {
        (PersonSort.Code, false) => people.OrderBy(p => p.CodeNumber),
        (PersonSort.Code, true) => people.OrderByDescending(p => p.CodeNumber),
        (PersonSort.Joined, false) => people.OrderBy(p => p.JoiningDate).ThenBy(p => p.CodeNumber),
        (PersonSort.Joined, true) => people.OrderByDescending(p => p.JoiningDate).ThenBy(p => p.CodeNumber),
        (_, true) => people.OrderByDescending(p => p.FullName).ThenBy(p => p.CodeNumber),
        _ => people.OrderBy(p => p.FullName).ThenBy(p => p.CodeNumber),
    };

    private async Task<List<PersonError>> DuplicateErrorsAsync(PersonInput normalized, int? exceptId, CancellationToken cancellationToken)
    {
        var errors = new List<PersonError>();
        if (normalized.Email is { } email && await db.People.AnyAsync(p => p.Email == email && p.Id != exceptId, cancellationToken))
        {
            errors.Add(new PersonError(nameof(PersonInput.Email), DuplicateEmailMessage));
        }

        if (normalized.Cnic is { } cnic && await db.People.AnyAsync(p => p.Cnic == cnic && p.Id != exceptId, cancellationToken))
        {
            errors.Add(new PersonError(nameof(PersonInput.Cnic), DuplicateCnicMessage));
        }

        return errors;
    }

    /// <summary>Saves; maps unique-index races and concurrency conflicts to friendly results. Null means success.</summary>
    private async Task<PersonResult?> SaveAsync(
        CancellationToken cancellationToken,
        int? conflictId = null,
        string? ownerMessage = null,
        string ownerField = nameof(Person.HireSource))
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException) when (conflictId is not null)
        {
            db.ChangeTracker.Clear();
            return await ConflictAsync(conflictId.Value, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: OverlapErrorNumber })
        {
            // The database trigger caught an overlap the domain should already have prevented.
            db.ChangeTracker.Clear();
            return PersonResult.Invalid(nameof(PersonInput.JoiningDate), "Employment periods can't overlap.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: ActiveOwnerErrorNumber })
        {
            // The database trigger caught a second active Owner (a race the checks above couldn't see).
            db.ChangeTracker.Clear();
            return PersonResult.Invalid(ownerField, ownerMessage ?? "This change can't be saved right now.");
        }
        catch (DbUpdateException ex) when (UniqueIndex(ex) is { } index)
        {
            db.ChangeTracker.Clear();
            return index switch
            {
                PersonConfiguration.EmailIndex => PersonResult.Invalid(nameof(PersonInput.Email), DuplicateEmailMessage),
                PersonConfiguration.CnicIndex => PersonResult.Invalid(nameof(PersonInput.Cnic), DuplicateCnicMessage),
                _ => PersonResult.Invalid(string.Empty, "This change can't be saved. Please try again."),
            };
        }
    }

    private async Task<PersonResult> ConflictAsync(int id, CancellationToken cancellationToken)
    {
        var current = await GetAsync(id, cancellationToken);
        return current is null
            ? new PersonResult(PersonResultStatus.NotFound)
            : new PersonResult(PersonResultStatus.Conflict, id, Current: current);
    }

    private static string? UniqueIndex(DbUpdateException ex)
    {
        if (ex.InnerException is not SqlException { Number: 2601 or 2627 } sql)
        {
            return null;
        }

        foreach (var index in new[] { PersonConfiguration.EmailIndex, PersonConfiguration.CnicIndex })
        {
            if (sql.Message.Contains(index, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }
}
