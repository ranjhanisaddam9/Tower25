namespace HR.Domain.Pay;

public enum PayCurrency
{
    USD = 1,
    PKR = 2,
}

/// <summary>Why a rate record exists. Derived on save; only an Admin may mark an edit as a <see cref="Correction"/>.</summary>
public enum RateChangeType
{
    Initial = 1,
    Increment = 2,
    Decrement = 3,
    BillingChange = 4,
    Correction = 5,
}

/// <summary>The money terms of one rate record (SPEC §2), already derived and validated.</summary>
public sealed record PayTerms(
    DateOnly EffectiveFrom,
    decimal BilledMonthlyUsd,
    decimal CommissionPerPeriodUsd,
    decimal PayMonthlyAmount,
    PayCurrency PayCurrency);

/// <summary>
/// A person's billing, commission and pay from <see cref="EffectiveFrom"/> (the 1st or 16th) until the next record.
/// The record in effect on a period's start date applies to the whole period (SPEC §2). Corrections are allowed and
/// audited; finalized payrolls keep their own snapshot (M7).
/// </summary>
public sealed class RateRecord
{
    public const int NoteMaxLength = 300;

    private RateRecord()
    {
    }

    public int Id { get; private set; }

    public int PersonId { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public decimal BilledMonthlyUsd { get; private set; }

    public decimal CommissionPerPeriodUsd { get; private set; }

    public decimal PayMonthlyAmount { get; private set; }

    public PayCurrency PayCurrency { get; private set; }

    public RateChangeType ChangeType { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Set when a Manager changes pay for a BudgetHire person: the budget may have changed too.</summary>
    public bool NeedsBillingReview { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = [];

    public PayTerms Terms => new(EffectiveFrom, BilledMonthlyUsd, CommissionPerPeriodUsd, PayMonthlyAmount, PayCurrency);

    public static RateRecord Create(int personId, PayTerms terms, string? note, bool needsBillingReview, string actorId, DateTimeOffset now)
    {
        var record = new RateRecord
        {
            PersonId = personId,
            ChangeType = RateChangeType.Initial, // re-derived against the person's other records before saving
            CreatedAt = now,
            CreatedByUserId = actorId,
        };
        record.Apply(terms, note, needsBillingReview, actorId, now);
        return record;
    }

    public void Update(PayTerms terms, string? note, bool needsBillingReview, string actorId, DateTimeOffset now) =>
        Apply(terms, note, needsBillingReview, actorId, now);

    public void SetChangeType(RateChangeType changeType) => ChangeType = changeType;

    public void MarkReviewed(string actorId, DateTimeOffset now)
    {
        NeedsBillingReview = false;
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }

    private void Apply(PayTerms terms, string? note, bool needsBillingReview, string actorId, DateTimeOffset now)
    {
        var errors = PayRules.TermErrors(terms);
        if (errors.Count > 0)
        {
            throw new ArgumentException(errors[0].Message, errors[0].Field);
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > NoteMaxLength })
        {
            throw new ArgumentException("The note can be at most 300 characters.", nameof(note));
        }

        EffectiveFrom = terms.EffectiveFrom;
        BilledMonthlyUsd = terms.BilledMonthlyUsd;
        CommissionPerPeriodUsd = terms.CommissionPerPeriodUsd;
        PayMonthlyAmount = terms.PayMonthlyAmount;
        PayCurrency = terms.PayCurrency;
        Note = trimmed;
        NeedsBillingReview = needsBillingReview;
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }
}
