namespace HR.Domain.Absences;

/// <summary>How much of a working day an absence covers (SPEC §4). Stored as its name.</summary>
public enum AbsencePortion
{
    Full = 1,
    Half = 2,
}

public static class AbsencePortions
{
    /// <summary>Full = 1.0 day, Half = 0.5 day.</summary>
    public static decimal Days(this AbsencePortion portion) => portion switch
    {
        AbsencePortion.Full => 1.0m,
        AbsencePortion.Half => 0.5m,
        _ => throw new ArgumentOutOfRangeException(nameof(portion), portion, "Unknown absence portion."),
    };

    /// <summary>Parses "Full" or "Half" (case-insensitive) only; numbers and anything else are rejected.</summary>
    public static bool TryParse(string? value, out AbsencePortion portion)
    {
        foreach (var name in Enum.GetNames<AbsencePortion>())
        {
            if (string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                portion = Enum.Parse<AbsencePortion>(name);
                return true;
            }
        }

        portion = default;
        return false;
    }
}

/// <summary>
/// One person's absence on one working day (SPEC §4). Whether it is paid is never stored: <see cref="PaidLeaveAllocator"/>
/// computes it from all of the person's absences in that month.
/// </summary>
public sealed class Absence
{
    public const int NoteMaxLength = 300;

    private Absence()
    {
    }

    public int Id { get; private set; }

    public int PersonId { get; private set; }

    public DateOnly Date { get; private set; }

    public AbsencePortion Portion { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Creates an absence. The date rules that need other data (employment, duplicates, locks) are checked by <see cref="AbsenceRules"/>.</summary>
    public static Absence Create(int personId, DateOnly date, AbsencePortion portion, string? note, string actorId, DateTimeOffset now)
    {
        if (!AbsenceRules.IsWeekday(date))
        {
            throw new ArgumentException(AbsenceRules.WeekendMessage, nameof(date));
        }

        var absence = new Absence
        {
            PersonId = personId,
            Date = date,
            CreatedAt = now,
            CreatedByUserId = actorId,
        };
        absence.Update(portion, note, actorId, now);
        return absence;
    }

    /// <summary>Changes the portion and note. The date never changes: delete and add instead.</summary>
    public void Update(AbsencePortion portion, string? note, string actorId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(portion))
        {
            throw new ArgumentOutOfRangeException(nameof(portion), portion, "Unknown absence portion.");
        }

        Portion = portion;
        Note = NormalizeNote(note);
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }

    public static string? NormalizeNote(string? note)
    {
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return trimmed is { Length: > NoteMaxLength }
            ? throw new ArgumentException(AbsenceRules.NoteTooLongMessage, nameof(note))
            : trimmed;
    }
}
