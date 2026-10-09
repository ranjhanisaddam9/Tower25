namespace HR.Domain.People;

/// <summary>
/// One stint of employment (SPEC §2). A person has one or more periods that never overlap, with at most one open
/// period (no end date). Periods are the source of truth for payroll; <see cref="Person.JoiningDate"/> and
/// <see cref="Person.LeavingDate"/> are cached copies of the latest period.
/// Created and changed only through <see cref="Person"/>, which keeps the cache in step.
/// </summary>
public sealed class EmploymentPeriod
{
    private EmploymentPeriod()
    {
    }

    internal EmploymentPeriod(DateOnly startDate, string actorId, DateTimeOffset now)
    {
        StartDate = startDate;
        CreatedAt = now;
        CreatedByUserId = actorId;
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }

    public int Id { get; private set; }

    public int PersonId { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly? EndDate { get; private set; }

    public bool IsOpen => EndDate is null;

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public EmploymentSpan Span => new(StartDate, EndDate);

    internal void Close(DateOnly endDate, string actorId, DateTimeOffset now)
    {
        EndDate = endDate;
        Touch(actorId, now);
    }

    internal void MoveStart(DateOnly startDate, string actorId, DateTimeOffset now)
    {
        StartDate = startDate;
        Touch(actorId, now);
    }

    private void Touch(string actorId, DateTimeOffset now)
    {
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }
}

/// <summary>A start date and optional end date (both inclusive). An open span has no end.</summary>
public readonly record struct EmploymentSpan(DateOnly Start, DateOnly? End)
{
    public bool Contains(DateOnly date) => date >= Start && (End is null || date <= End);
}

/// <summary>The rules every person's set of periods must satisfy (also enforced in the database).</summary>
public static class EmploymentHistory
{
    /// <summary>Throws <see cref="PersonRuleException"/> if any rule is broken. Order of the input does not matter.</summary>
    public static void EnsureValid(IEnumerable<EmploymentSpan> spans)
    {
        var ordered = spans.OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0)
        {
            throw new PersonRuleException("JoiningDate", "A person needs at least one employment period.");
        }

        if (ordered.Count(s => s.End is null) > 1)
        {
            throw new PersonRuleException("JoiningDate", "A person can have only one open employment period.");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var span = ordered[i];
            if (span.End is { } end && end < span.Start)
            {
                throw new PersonRuleException("LeavingDate", "An employment period can't end before it starts.");
            }

            if (i == 0)
            {
                continue;
            }

            var previous = ordered[i - 1];
            if (previous.End is null)
            {
                throw new PersonRuleException("JoiningDate", "Only the latest employment period can be open.");
            }

            if (span.Start <= previous.End)
            {
                throw new PersonRuleException("JoiningDate",
                    $"Employment periods can't overlap: a period must start after the previous one ended ({previous.End:yyyy-MM-dd}).");
            }
        }
    }
}
