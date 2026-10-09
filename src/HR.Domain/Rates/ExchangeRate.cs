namespace HR.Domain.Rates;

/// <summary>
/// A USD→PKR rate effective from a date until the next entry (SPEC §2). Corrections (edit/delete) are allowed;
/// payroll snapshots its own rate when finalized (M7), so a correction never changes a finalized payroll.
/// </summary>
public sealed class ExchangeRate
{
    private ExchangeRate()
    {
    }

    public int Id { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>PKR for 1 USD, up to 4 decimal places, between 100 and 1000.</summary>
    public decimal UsdToPkr { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = [];

    public static ExchangeRate Create(DateOnly effectiveFrom, decimal usdToPkr, string? note, string actorId, DateTimeOffset now)
    {
        var rate = new ExchangeRate { CreatedAt = now, CreatedByUserId = actorId };
        rate.Apply(effectiveFrom, usdToPkr, note, actorId, now);
        return rate;
    }

    public void Update(DateOnly effectiveFrom, decimal usdToPkr, string? note, string actorId, DateTimeOffset now) =>
        Apply(effectiveFrom, usdToPkr, note, actorId, now);

    private void Apply(DateOnly effectiveFrom, decimal usdToPkr, string? note, string actorId, DateTimeOffset now)
    {
        if (ExchangeRateRules.RateError(usdToPkr) is { } error)
        {
            throw new ArgumentOutOfRangeException(nameof(usdToPkr), error);
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > ExchangeRateRules.NoteMaxLength })
        {
            throw new ArgumentOutOfRangeException(nameof(note), "The note can be at most 200 characters.");
        }

        EffectiveFrom = effectiveFrom;
        UsdToPkr = usdToPkr;
        Note = trimmed;
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }
}
