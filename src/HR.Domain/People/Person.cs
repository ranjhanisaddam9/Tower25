namespace HR.Domain.People;

/// <summary>
/// An employee or internee placed at the Company (SPEC §2).
/// Invariants: LeavingDate ≥ JoiningDate; the code never changes. Active status is not stored: a person is active until
/// their leaving date has passed (<see cref="IsActiveOn"/>, <see cref="PersonStatus"/>).
/// </summary>
public sealed class Person
{
    private readonly List<EmploymentPeriod> _periods = [];

    private Person()
    {
    }

    public int Id { get; private set; }

    /// <summary>The sequence number behind <see cref="Code"/>; used for sorting.</summary>
    public int CodeNumber { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string FullName { get; private set; } = string.Empty;

    public PersonType Type { get; private set; }

    public string Designation { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public string Phone { get; private set; } = string.Empty;

    public string? Cnic { get; private set; }

    public string? BankName { get; private set; }

    public string? Iban { get; private set; }

    public DateOnly JoiningDate { get; private set; }

    public DateOnly? LeavingDate { get; private set; }

    /// <summary>Active ⇔ no leaving date, or the leaving date is today or later (Asia/Karachi "today").</summary>
    public bool IsActiveOn(DateOnly today) => LeavingDate is not { } leaving || leaving >= today;

    /// <summary>Admin-only. Null means "not assigned yet".</summary>
    public HireSource? HireSource { get; private set; }

    public string? Notes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    /// <summary>Optimistic-concurrency token, maintained by the database.</summary>
    public byte[] RowVersion { get; private set; } = [];

    public static Person Create(int codeNumber, PersonInput input, string actorId, DateTimeOffset now)
    {
        var person = new Person
        {
            CodeNumber = codeNumber,
            Code = PersonCode.Format(codeNumber),
            CreatedAt = now,
            CreatedByUserId = actorId,
        };
        var normalized = Normalized(input);
        person.Apply(normalized, actorId, now);
        person._periods.Add(new EmploymentPeriod(normalized.JoiningDate!.Value, actorId, now)); // the first period, open
        return person;
    }

    /// <summary>
    /// Updates the details. The joining date edits the latest period's start, which must stay after the previous
    /// period's end and on or before its own end. Requires <see cref="EmploymentPeriods"/> to be loaded.
    /// </summary>
    public void UpdateDetails(PersonInput input, string actorId, DateTimeOffset now)
    {
        var normalized = Normalized(input);
        var latest = LatestPeriod();
        var newStart = normalized.JoiningDate!.Value;
        if (latest.EndDate is { } end && newStart > end)
        {
            throw new PersonRuleException(nameof(JoiningDate), "The joining date can't be after the leaving date.");
        }

        EmploymentHistory.EnsureValid(_periods.Select(p => p == latest ? p.Span with { Start = newStart } : p.Span));

        latest.MoveStart(newStart, actorId, now);
        Apply(normalized, actorId, now);
    }

    /// <summary>
    /// Sets a leaving date by closing the open employment period. The date may be in the past or the future; the person
    /// stays active until it has passed. Requires <see cref="EmploymentPeriods"/> to be loaded.
    /// </summary>
    public void Deactivate(DateOnly leavingDate, string actorId, DateTimeOffset now)
    {
        var open = LatestPeriod();
        if (!open.IsOpen)
        {
            throw new PersonRuleException(nameof(LeavingDate), "This person already has a leaving date.");
        }

        if (leavingDate < open.StartDate)
        {
            throw new PersonRuleException(nameof(LeavingDate), "The leaving date can't be before the joining date.");
        }

        open.Close(leavingDate, actorId, now);
        LeavingDate = leavingDate;
        Touch(actorId, now);
    }

    /// <summary>
    /// Cancels a leaving date that hasn't passed yet: the same employment period is reopened (no new period).
    /// Returns the cancelled date. Requires <see cref="EmploymentPeriods"/> to be loaded.
    /// </summary>
    public DateOnly CancelLeaving(DateOnly today, string actorId, DateTimeOffset now)
    {
        var latest = LatestPeriod();
        if (latest.EndDate is not { } leaving)
        {
            throw new PersonRuleException(nameof(LeavingDate), "This person has no leaving date to cancel.");
        }

        if (leaving < today)
        {
            throw new PersonRuleException(nameof(LeavingDate), "The leaving date has already passed. Use Reactivate to record a rejoining date.");
        }

        latest.Reopen(actorId, now);
        LeavingDate = null;
        Touch(actorId, now);
        return leaving;
    }

    /// <summary>
    /// Opens a new employment period starting on <paramref name="rejoiningDate"/>, which must be after the previous
    /// period's end. Returns the previous period's dates for the audit log. Requires <see cref="EmploymentPeriods"/> to be loaded.
    /// </summary>
    public (DateOnly PreviousJoiningDate, DateOnly? PreviousLeavingDate) Reactivate(DateOnly rejoiningDate, string actorId, DateTimeOffset now)
    {
        var previous = LatestPeriod();
        if (previous.EndDate is not { } leaving)
        {
            throw new PersonRuleException("RejoiningDate", "This person has no leaving date, so there is nothing to rejoin from.");
        }

        if (rejoiningDate <= leaving)
        {
            throw new PersonRuleException("RejoiningDate", "The rejoining date must be after the previous leaving date.");
        }

        EmploymentHistory.EnsureValid(_periods.Select(p => p.Span).Append(new EmploymentSpan(rejoiningDate, null)));

        _periods.Add(new EmploymentPeriod(rejoiningDate, actorId, now));
        JoiningDate = rejoiningDate;
        LeavingDate = null;
        Touch(actorId, now);
        return (previous.StartDate, previous.EndDate);
    }

    /// <summary>All employment periods (load them with Include before calling the methods that change them).</summary>
    public IReadOnlyCollection<EmploymentPeriod> EmploymentPeriods => _periods;

    private EmploymentPeriod LatestPeriod() =>
        _periods.MaxBy(p => p.StartDate)
        ?? throw new InvalidOperationException("Employment periods are not loaded for this person.");

    public void SetHireSource(HireSource? source, string actorId, DateTimeOffset now)
    {
        if (source is { } value && !Enum.IsDefined(value))
        {
            throw new PersonRuleException(nameof(HireSource), "Unknown hire source.");
        }

        HireSource = source;
        Touch(actorId, now);
    }

    private static PersonInput Normalized(PersonInput input)
    {
        var normalized = input.Normalize(out var errors);
        if (normalized is null)
        {
            var first = errors[0];
            throw new PersonRuleException(first.Field, first.Message);
        }

        return normalized;
    }

    private void Apply(PersonInput input, string actorId, DateTimeOffset now)
    {
        FullName = input.FullName!;
        Type = input.Type!.Value;
        Designation = input.Designation!;
        Email = input.Email;
        Phone = input.Phone!;
        Cnic = input.Cnic;
        BankName = input.BankName;
        Iban = input.Iban;
        JoiningDate = input.JoiningDate!.Value;
        Notes = input.Notes;
        Touch(actorId, now);
    }

    private void Touch(string actorId, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }
}
