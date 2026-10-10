using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.People;

namespace HR.Domain.Payroll;

public enum PayrollStatus
{
    Draft = 1,
    Finalized = 2,
}

public enum PayrollEventType
{
    Generated = 1,
    Regenerated = 2,
    RateChanged = 3,
    Finalized = 4,
    Reopened = 5,
}

/// <summary>
/// One payroll for one pay period (SPEC §6). Draft → Finalized; only the latest finalized run can be reopened (Admin,
/// with a reason). A finalized run is never deleted (service and database trigger) and its lines are a frozen snapshot.
/// </summary>
public sealed class PayrollRun
{
    public const int RateNoteMaxLength = 300;
    public const int ReopenReasonMinLength = 10;
    public const int ReopenReasonMaxLength = 500;

    private readonly List<PayrollLine> _lines = [];
    private readonly List<PayrollRunEvent> _events = [];

    private PayrollRun()
    {
    }

    public int Id { get; private set; }

    public DateOnly PeriodStart { get; private set; }

    public DateOnly PeriodEnd { get; private set; }

    public PayrollStatus Status { get; private set; }

    /// <summary>USD → PKR. Null until a rate exists ("Set exchange rate"); required to finalize.</summary>
    public decimal? ExchangeRate { get; private set; }

    /// <summary>The exchange-rate history entry the rate was proposed from; null when overridden or none.</summary>
    public int? ExchangeRateEntryId { get; private set; }

    public bool RateOverridden { get; private set; }

    public string? RateNote { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public string GeneratedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset? FinalizedAt { get; private set; }

    public string? FinalizedByUserId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<PayrollLine> Lines => _lines;

    public IReadOnlyCollection<PayrollRunEvent> Events => _events;

    public PayPeriod Period => PayPeriod.For(PeriodStart);

    public bool IsDraft => Status == PayrollStatus.Draft;

    public static PayrollRun Create(PayPeriod period, decimal? rate, int? rateEntryId, string actorId, DateTimeOffset now)
    {
        var run = new PayrollRun
        {
            PeriodStart = period.Start,
            PeriodEnd = period.End,
            Status = PayrollStatus.Draft,
            ExchangeRate = rate,
            ExchangeRateEntryId = rate is null ? null : rateEntryId,
            GeneratedAt = now,
            GeneratedByUserId = actorId,
        };
        run._events.Add(new PayrollRunEvent(PayrollEventType.Generated, null, actorId, now));
        return run;
    }

    public PayrollLine AddLine(int personId)
    {
        EnsureDraft();
        var line = PayrollLine.Create(personId);
        _lines.Add(line);
        return line;
    }

    public void RemoveLine(PayrollLine line)
    {
        EnsureDraft();
        _lines.Remove(line);
    }

    public void MarkRegenerated(string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        GeneratedAt = now;
        GeneratedByUserId = actorId;
        _events.Add(new PayrollRunEvent(PayrollEventType.Regenerated, null, actorId, now));
    }

    /// <summary>Sets the rate. From the history (<paramref name="entryId"/> set) or overridden with a note.</summary>
    public void SetRate(decimal rate, int? entryId, string? note, string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "The exchange rate must be positive.");
        }

        var old = ExchangeRate;
        ExchangeRate = rate;
        ExchangeRateEntryId = entryId;
        RateOverridden = entryId is null;
        RateNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        _events.Add(new PayrollRunEvent(PayrollEventType.RateChanged,
            $"{old?.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) ?? "none"} -> {rate.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}", actorId, now));
    }

    public void Finalize(string actorId, DateTimeOffset now)
    {
        EnsureDraft();
        if (ExchangeRate is null)
        {
            throw new InvalidOperationException("A payroll can't be finalized without an exchange rate.");
        }

        Status = PayrollStatus.Finalized;
        FinalizedAt = now;
        FinalizedByUserId = actorId;
        _events.Add(new PayrollRunEvent(PayrollEventType.Finalized, null, actorId, now));
    }

    public void Reopen(string reason, string actorId, DateTimeOffset now)
    {
        if (Status != PayrollStatus.Finalized)
        {
            throw new InvalidOperationException("Only a finalized payroll can be reopened.");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length < ReopenReasonMinLength || trimmed.Length > ReopenReasonMaxLength)
        {
            throw new ArgumentException("The reason must be 10 to 500 characters.", nameof(reason));
        }

        Status = PayrollStatus.Draft;
        FinalizedAt = null;
        FinalizedByUserId = null;
        _events.Add(new PayrollRunEvent(PayrollEventType.Reopened, trimmed, actorId, now));
    }

    private void EnsureDraft()
    {
        if (Status != PayrollStatus.Draft)
        {
            throw new InvalidOperationException("A finalized payroll can't be changed. Reopen it first.");
        }
    }
}

/// <summary>History of a run: who generated, regenerated, changed the rate, finalized or reopened it (with the reason).</summary>
public sealed class PayrollRunEvent
{
    public const int DetailMaxLength = 500;

    private PayrollRunEvent()
    {
    }

    internal PayrollRunEvent(PayrollEventType type, string? detail, string actorId, DateTimeOffset at)
    {
        Type = type;
        Detail = detail;
        ActorId = actorId;
        At = at;
    }

    public int Id { get; private set; }

    public int RunId { get; private set; }

    public PayrollEventType Type { get; private set; }

    /// <summary>The reopen reason, or "old -> new" for a rate change.</summary>
    public string? Detail { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public DateTimeOffset At { get; private set; }
}

/// <summary>
/// One person's line: a full snapshot of who they were, the rate record used and every calculated value, so a
/// finalized payroll never changes when people, rates or exchange rates change later.
/// </summary>
public sealed class PayrollLine
{
    public const int ExtraDaysNoteMaxLength = 300;

    private readonly List<PayrollAdjustment> _adjustments = [];
    private readonly List<PayrollLineAbsence> _absences = [];

    private PayrollLine()
    {
    }

    public int Id { get; private set; }

    public int RunId { get; private set; }

    public int PersonId { get; private set; }

    // Person snapshot
    public string PersonCode { get; private set; } = string.Empty;

    public string PersonName { get; private set; } = string.Empty;

    public string Designation { get; private set; } = string.Empty;

    public PersonType PersonType { get; private set; }

    public HireSource? HireSource { get; private set; }

    public string? BankName { get; private set; }

    public string? Iban { get; private set; }

    // Rate record snapshot
    public int? RateRecordId { get; private set; }

    public decimal? BilledMonthlyUsd { get; private set; }

    public decimal? CommissionPerPeriodUsd { get; private set; }

    public decimal? PayMonthlyAmount { get; private set; }

    public PayCurrency? PayCurrency { get; private set; }

    // Entered on the draft
    public decimal ExtraDays { get; private set; }

    public string? ExtraDaysNote { get; private set; }

    // Calculator outputs
    public int WorkingDays { get; private set; }

    public int EmployedWorkingDays { get; private set; }

    public decimal UnpaidDays { get; private set; }

    public decimal PayableDays { get; private set; }

    public decimal? SalaryPartUsd { get; private set; }

    public decimal? CommissionUsd { get; private set; }

    public decimal? BilledUsd { get; private set; }

    public decimal? PayUsd { get; private set; }

    public decimal? PayPkr { get; private set; }

    public decimal? AdjustmentsPkr { get; private set; }

    public decimal? AdjustmentsUsd { get; private set; }

    public decimal? NetPayPkr { get; private set; }

    public decimal? NetPayUsd { get; private set; }

    public decimal? InvoiceUsd { get; private set; }

    public decimal? OwnerEarningUsd { get; private set; }

    public decimal? OwnerEarningPkr { get; private set; }

    public LineIssue? Issue { get; private set; }

    /// <summary>The person no longer has an employed working day in the period but the line still has extra days or adjustments.</summary>
    public bool IsOrphaned { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyCollection<PayrollAdjustment> Adjustments => _adjustments;

    public IReadOnlyCollection<PayrollLineAbsence> Absences => _absences;

    public static PayrollLine Create(int personId) => new() { PersonId = personId };

    /// <summary>Writes the snapshot: person, rate record and every calculator output (and the absences of the period).</summary>
    public void Apply(PayrollLineSnapshot snapshot, PayrollLineResult result, bool orphaned)
    {
        PersonCode = snapshot.PersonCode;
        PersonName = snapshot.PersonName;
        Designation = snapshot.Designation;
        PersonType = snapshot.PersonType;
        HireSource = snapshot.HireSource;
        BankName = snapshot.BankName;
        Iban = snapshot.Iban;
        RateRecordId = snapshot.RateRecordId;
        BilledMonthlyUsd = snapshot.Terms?.BilledMonthlyUsd;
        CommissionPerPeriodUsd = snapshot.Terms?.CommissionPerPeriodUsd;
        PayMonthlyAmount = snapshot.Terms?.PayMonthlyAmount;
        PayCurrency = snapshot.Terms?.PayCurrency;

        WorkingDays = result.WorkingDays;
        EmployedWorkingDays = result.EmployedWorkingDays;
        UnpaidDays = result.UnpaidDays;
        PayableDays = result.PayableDays;
        SalaryPartUsd = result.SalaryPartUsd;
        CommissionUsd = result.CommissionUsd;
        BilledUsd = result.BilledUsd;
        PayUsd = result.PayUsd;
        PayPkr = result.PayPkr;
        AdjustmentsPkr = result.AdjustmentsPkr;
        AdjustmentsUsd = result.AdjustmentsUsd;
        NetPayPkr = result.NetPayPkr;
        NetPayUsd = result.NetPayUsd;
        InvoiceUsd = result.InvoiceUsd;
        OwnerEarningUsd = result.OwnerEarningUsd;
        OwnerEarningPkr = result.OwnerEarningPkr;
        Issue = result.Issue;
        IsOrphaned = orphaned;

        // The adjustments keep their order; their converted amounts follow the result.
        var ordered = OrderedAdjustments();
        for (var i = 0; i < ordered.Count && i < result.AdjustmentAmounts.Count; i++)
        {
            ordered[i].SetConverted(result.AdjustmentAmounts[i].AmountPkr, result.AdjustmentAmounts[i].AmountUsd);
        }

        var wanted = result.PeriodAbsences.Select(a => (a.Date, a.Portion, a.PaidDays, a.UnpaidDays)).ToList();
        if (!_absences.Select(a => (a.Date, a.Portion, a.PaidDays, a.UnpaidDays)).OrderBy(a => a.Date).SequenceEqual(wanted.OrderBy(a => a.Date)))
        {
            _absences.Clear();
            _absences.AddRange(wanted.Select(a => new PayrollLineAbsence(a.Date, a.Portion, a.PaidDays, a.UnpaidDays)));
        }
    }

    public void SetExtraDays(decimal days, string? note)
    {
        if (!ExtraDaysRules.IsValidStored(days))
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, ExtraDaysRules.RangeMessage);
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > ExtraDaysNoteMaxLength })
        {
            throw new ArgumentException("The note can be at most 300 characters.", nameof(note));
        }

        ExtraDays = days;
        ExtraDaysNote = days == 0m ? null : trimmed;
    }

    public PayrollAdjustment AddAdjustment(AdjustmentType type, decimal amount, PayCurrency currency, string? note, string actorId, DateTimeOffset now)
    {
        var adjustment = PayrollAdjustment.Create(type, amount, currency, note, actorId, now);
        _adjustments.Add(adjustment);
        return adjustment;
    }

    public void RemoveAdjustment(PayrollAdjustment adjustment) => _adjustments.Remove(adjustment);

    /// <summary>The adjustments as calculator input, in their stored order.</summary>
    public IReadOnlyList<AdjustmentInput> AdjustmentInputs() =>
        OrderedAdjustments().Select(a => new AdjustmentInput(a.Type, a.Amount, a.Currency)).ToList();

    /// <summary>Saved adjustments by id, then new ones in the order they were added (OrderBy is stable).</summary>
    public List<PayrollAdjustment> OrderedAdjustments() => _adjustments.OrderBy(a => a.Id == 0 ? int.MaxValue : a.Id).ToList();

    public bool HasEntries => ExtraDays != 0m || _adjustments.Count > 0;
}

/// <summary>The person and rate-record values copied onto a line.</summary>
public sealed record PayrollLineSnapshot(
    string PersonCode,
    string PersonName,
    string Designation,
    PersonType PersonType,
    HireSource? HireSource,
    string? BankName,
    string? Iban,
    int? RateRecordId,
    PayTerms? Terms);

/// <summary>An absence in the line's period with its paid/unpaid split, as calculated for the line.</summary>
public sealed class PayrollLineAbsence
{
    private PayrollLineAbsence()
    {
    }

    internal PayrollLineAbsence(DateOnly date, AbsencePortion portion, decimal paidDays, decimal unpaidDays)
    {
        Date = date;
        Portion = portion;
        PaidDays = paidDays;
        UnpaidDays = unpaidDays;
    }

    public int Id { get; private set; }

    public int LineId { get; private set; }

    public DateOnly Date { get; private set; }

    public AbsencePortion Portion { get; private set; }

    public decimal PaidDays { get; private set; }

    public decimal UnpaidDays { get; private set; }
}

/// <summary>
/// A Bonus, Reimbursement or Deduction on a draft line (SPEC §5). Not prorated; passes through to the Company at cost.
/// The converted amounts are recomputed while the run is a draft and frozen when it is finalized.
/// </summary>
public sealed class PayrollAdjustment
{
    private PayrollAdjustment()
    {
    }

    public int Id { get; private set; }

    public int LineId { get; private set; }

    public AdjustmentType Type { get; private set; }

    public decimal Amount { get; private set; }

    public PayCurrency Currency { get; private set; }

    public string? Note { get; private set; }

    public decimal? AmountPkr { get; private set; }

    public decimal? AmountUsd { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedByUserId { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public string UpdatedByUserId { get; private set; } = string.Empty;

    public byte[] RowVersion { get; private set; } = [];

    internal static PayrollAdjustment Create(AdjustmentType type, decimal amount, PayCurrency currency, string? note, string actorId, DateTimeOffset now)
    {
        var adjustment = new PayrollAdjustment { CreatedAt = now, CreatedByUserId = actorId };
        adjustment.Update(type, amount, currency, note, actorId, now);
        return adjustment;
    }

    public void Update(AdjustmentType type, decimal amount, PayCurrency currency, string? note, string actorId, DateTimeOffset now)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown adjustment type.");
        }

        if (AdjustmentRules.AmountError(amount, currency) is { } error)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, error);
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > AdjustmentRules.NoteMaxLength })
        {
            throw new ArgumentException("The note can be at most 300 characters.", nameof(note));
        }

        Type = type;
        Amount = amount;
        Currency = currency;
        Note = trimmed;
        UpdatedAt = now;
        UpdatedByUserId = actorId;
    }

    internal void SetConverted(decimal? amountPkr, decimal? amountUsd)
    {
        AmountPkr = amountPkr;
        AmountUsd = amountUsd;
    }

    public int Sign => Type == AdjustmentType.Deduction ? -1 : 1;
}
