namespace HR.Domain.People;

/// <summary>
/// An employee or internee placed at the Company (SPEC §2).
/// Invariants: LeavingDate ≥ JoiningDate; inactive ⇔ a leaving date is set; the code never changes.
/// </summary>
public sealed class Person
{
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

    public bool IsActive { get; private set; }

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
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = actorId,
        };
        person.Apply(Normalized(input), actorId, now);
        return person;
    }

    public void UpdateDetails(PersonInput input, string actorId, DateTimeOffset now)
    {
        var normalized = Normalized(input);
        if (LeavingDate is { } leaving && normalized.JoiningDate > leaving)
        {
            throw new PersonRuleException(nameof(JoiningDate), "The joining date can't be after the leaving date.");
        }

        Apply(normalized, actorId, now);
    }

    public void Deactivate(DateOnly leavingDate, string actorId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            throw new PersonRuleException(nameof(LeavingDate), "This person is already inactive.");
        }

        if (leavingDate < JoiningDate)
        {
            throw new PersonRuleException(nameof(LeavingDate), "The leaving date can't be before the joining date.");
        }

        LeavingDate = leavingDate;
        IsActive = false;
        Touch(actorId, now);
    }

    /// <summary>Starts a new stint: the rejoining date becomes the joining date. Returns the previous dates for the audit log.</summary>
    public (DateOnly PreviousJoiningDate, DateOnly? PreviousLeavingDate) Reactivate(DateOnly rejoiningDate, string actorId, DateTimeOffset now)
    {
        if (IsActive)
        {
            throw new PersonRuleException("RejoiningDate", "This person is already active.");
        }

        if (LeavingDate is { } leaving && rejoiningDate <= leaving)
        {
            throw new PersonRuleException("RejoiningDate", "The rejoining date must be after the previous leaving date.");
        }

        var previous = (JoiningDate, LeavingDate);
        JoiningDate = rejoiningDate;
        LeavingDate = null;
        IsActive = true;
        Touch(actorId, now);
        return previous;
    }

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
