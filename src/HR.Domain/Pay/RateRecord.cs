namespace HR.Domain.Pay;

/// <summary>Currency of a payroll adjustment (bonus, reimbursement, deduction). Salaries are always USD (SPEC §2).</summary>
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

/// <summary>
/// The money terms of one rate record (SPEC §2), already derived and validated: three monthly USD figures with
/// Billed = Salary + Commission. The person is paid the salary (in PKR at the payroll's rate); the Company is billed.
/// </summary>
public sealed record PayTerms(
    DateOnly EffectiveFrom,
    decimal SalaryMonthlyUsd,
    decimal CommissionMonthlyUsd,
    decimal BilledMonthlyUsd);

/// <summary>
/// A person's salary, commission and billing (monthly USD) from <see cref="EffectiveFrom"/> (the 1st or 16th) until the
/// next record. The record in effect on a period's start date applies to the whole period (SPEC §2). Corrections are
/// allowed and audited; finalized payrolls keep their own snapshot (M7).
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

    public decimal SalaryMonthlyUsd { get; private set; }

    public decimal CommissionMonthlyUsd { get; private set; }

    /// <summary>Always SalaryMonthlyUsd + CommissionMonthlyUsd (also a database check constraint).</summary>
    public decimal BilledMonthlyUsd { get; private set; }

    public RateChangeType ChangeType { get; private set; }

    public string? Note { get; private set; }

    /// <summary>
    /// Set when a Manager changes the salary of a BudgetHire person (the budget may have changed too), and on records
    /// converted from PKR pay by the pay-model migration.
    /// </summary>
    public bool NeedsBillingReview { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = [];

    public PayTerms Terms => new(EffectiveFrom, SalaryMonthlyUsd, CommissionMonthlyUsd, BilledMonthlyUsd);

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
        SalaryMonthlyUsd = terms.SalaryMonthlyUsd;
        CommissionMonthlyUsd = terms.CommissionMonthlyUsd;
        BilledMonthlyUsd = terms.BilledMonthlyUsd;
        Note = trimmed;
        NeedsBillingReview = needsBillingReview;
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }
}
