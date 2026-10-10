using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.People;

namespace HR.Domain.Payroll;

public enum AdjustmentType
{
    Bonus = 1,
    Reimbursement = 2,
    Deduction = 3,
}

/// <summary>Why a line can't be finalized. Managers see both as "Pay setup incomplete" wording without the source.</summary>
public enum LineIssue
{
    NoHireSource = 1,
    NoRateRecordAtPeriodStart = 2,
}

public sealed record AdjustmentInput(AdjustmentType Type, decimal Amount, PayCurrency Currency);

/// <summary>An adjustment converted at the payroll's rate (SPEC §5). Null amounts: no rate yet.</summary>
public sealed record AdjustmentAmounts(AdjustmentType Type, decimal Amount, PayCurrency Currency, decimal? AmountPkr, decimal? AmountUsd)
{
    /// <summary>−1 for a deduction, +1 otherwise.</summary>
    public int Sign => Type == AdjustmentType.Deduction ? -1 : 1;
}

/// <summary>Everything one payroll line needs (SPEC §5).</summary>
/// <param name="MonthAbsences">All of the person's absences in the period's calendar month (paid leave runs across both periods).</param>
/// <param name="RateRecord">The record in effect on the period's start date, or null.</param>
public sealed record PayrollInput(
    PayPeriod Period,
    IReadOnlyList<EmploymentSpan> Employment,
    IReadOnlyList<AbsenceDay> MonthAbsences,
    PayTerms? RateRecord,
    HireSource? HireSource,
    decimal? ExchangeRate,
    decimal ExtraDays,
    IReadOnlyList<AdjustmentInput> Adjustments);

/// <summary>
/// Every intermediate value of a line. Day counts are always known. Money is null when there is no rate record, and the
/// values that need a conversion are null until the payroll has an exchange rate.
/// </summary>
public sealed record PayrollLineResult(
    int WorkingDays,
    int EmployedWorkingDays,
    decimal UnpaidDays,
    decimal ExtraDays,
    decimal PayableDays,
    decimal? SalaryPartUsd,
    decimal? CommissionUsd,
    decimal? BilledUsd,
    decimal? PayUsd,
    decimal? PayPkr,
    decimal? AdjustmentsPkr,
    decimal? AdjustmentsUsd,
    decimal? NetPayPkr,
    decimal? NetPayUsd,
    decimal? InvoiceUsd,
    decimal? OwnerEarningUsd,
    decimal? OwnerEarningPkr,
    LineIssue? Issue,
    IReadOnlyList<AdjustmentAmounts> AdjustmentAmounts,
    IReadOnlyList<AllocatedAbsence> PeriodAbsences);

/// <summary>Extra days worked (SPEC §5): 0.5 to 10 in steps of 0.5. Zero means none.</summary>
public static class ExtraDaysRules
{
    public const decimal Min = 0.5m;
    public const decimal Max = 10m;
    public const string RangeMessage = "Extra days must be between 0.5 and 10, in steps of 0.5.";

    /// <summary>The error for an entered value, or null when it is allowed. Zero is not an entry: clear the extra days instead.</summary>
    public static string? Error(decimal days) =>
        days < Min || days > Max || days * 2 != decimal.Truncate(days * 2) ? RangeMessage : null;

    /// <summary>Valid as stored on a line: zero (none) or a valid entry.</summary>
    public static bool IsValidStored(decimal days) => days == 0m || Error(days) is null;
}

/// <summary>Adjustment amount limits: USD 0.01–100,000 (cents); PKR 1–50,000,000 whole rupees.</summary>
public static class AdjustmentRules
{
    public const decimal MaxUsd = 100_000m;
    public const decimal MaxPkr = 50_000_000m;
    public const int NoteMaxLength = 300;

    public static string? AmountError(decimal amount, PayCurrency currency) => currency switch
    {
        PayCurrency.USD when amount < 0.01m || amount > MaxUsd => "A USD amount must be between 0.01 and 100,000.",
        PayCurrency.USD when amount != Math.Round(amount, 2) => "A USD amount can have at most 2 decimals.",
        PayCurrency.PKR when amount < 1m || amount > MaxPkr => "A PKR amount must be between 1 and 50,000,000.",
        PayCurrency.PKR when amount != decimal.Truncate(amount) => "PKR amounts must be in whole rupees.",
        PayCurrency.USD or PayCurrency.PKR => null,
        _ => "Choose USD or PKR.",
    };
}

/// <summary>
/// The payroll line calculation, exactly as SPEC §5: payable days = employed working days − unpaid absence days + extra
/// days; every amount scales by payable / working days; each component is rounded (AwayFromZero) before it is added;
/// adjustments pass through to the Company at cost.
/// </summary>
public static class PayrollCalculator
{
    public static PayrollLineResult Calculate(PayrollInput input)
    {
        if (!ExtraDaysRules.IsValidStored(input.ExtraDays))
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ExtraDays, ExtraDaysRules.RangeMessage);
        }

        if (input.ExchangeRate is { } r && r <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(input), r, "The exchange rate must be positive.");
        }

        var period = input.Period;
        var allocated = PaidLeaveAllocator.Allocate(input.MonthAbsences);
        var days = PayableDays.For(input.Employment, allocated, period);
        var payable = days.PayableDays + input.ExtraDays;
        var working = days.WorkingDays;
        var periodAbsences = allocated.Where(a => period.Contains(a.Date)).ToList();

        LineIssue? issue = input.HireSource is null ? LineIssue.NoHireSource
            : input.RateRecord is null ? LineIssue.NoRateRecordAtPeriodStart
            : null;

        var rate = input.ExchangeRate;
        var adjustments = input.Adjustments.Select(a => Convert(a, rate)).ToList();

        if (input.RateRecord is not { } terms)
        {
            return new PayrollLineResult(working, days.EmployedWorkingDays, days.UnpaidDays, input.ExtraDays, payable,
                null, null, null, null, null, null, null, null, null, null, null, null, issue, adjustments, periodAbsences);
        }

        // amount × payable / working: multiply first so the only inexact step is the final division.
        decimal Scale(decimal amount) => working == 0 ? 0m : amount * payable / working;

        var salaryPart = Money.RoundUsd(Scale(terms.BilledMonthlyUsd / 2m));
        var commission = Money.RoundUsd(Scale(terms.CommissionPerPeriodUsd));
        var billed = salaryPart + commission;

        decimal? payUsd;
        decimal? payPkr;
        if (terms.PayCurrency == PayCurrency.USD)
        {
            payUsd = Money.RoundUsd(Scale(terms.PayMonthlyAmount / 2m));
            payPkr = rate is { } x ? Money.RoundPkr(payUsd.Value * x) : null;
        }
        else
        {
            payPkr = Money.RoundPkr(Scale(terms.PayMonthlyAmount / 2m));
            payUsd = rate is { } x ? Money.RoundUsd(payPkr.Value / x) : null;
        }

        decimal? adjustmentsPkr = adjustments.All(a => a.AmountPkr is not null) ? adjustments.Sum(a => a.Sign * a.AmountPkr!.Value) : null;
        decimal? adjustmentsUsd = adjustments.All(a => a.AmountUsd is not null) ? adjustments.Sum(a => a.Sign * a.AmountUsd!.Value) : null;

        var netPayPkr = payPkr + adjustmentsPkr;
        var netPayUsd = payUsd + adjustmentsUsd;
        var invoiceUsd = billed + adjustmentsUsd;
        var ownerUsd = invoiceUsd - netPayUsd;
        decimal? ownerPkr = rate is { } y && invoiceUsd is { } invoice && netPayPkr is { } net ? Money.RoundPkr(invoice * y) - net : null;

        return new PayrollLineResult(working, days.EmployedWorkingDays, days.UnpaidDays, input.ExtraDays, payable,
            salaryPart, commission, billed, payUsd, payPkr, adjustmentsPkr, adjustmentsUsd, netPayPkr, netPayUsd, invoiceUsd,
            ownerUsd, ownerPkr, issue, adjustments, periodAbsences);
    }

    /// <summary>AmountPkr = PKR ? Amount : round0(Amount × rate); AmountUsd = USD ? Amount : round2(Amount / rate).</summary>
    public static AdjustmentAmounts Convert(AdjustmentInput adjustment, decimal? rate) => adjustment.Currency switch
    {
        PayCurrency.PKR => new AdjustmentAmounts(adjustment.Type, adjustment.Amount, adjustment.Currency,
            adjustment.Amount, rate is { } r ? Money.RoundUsd(adjustment.Amount / r) : null),
        _ => new AdjustmentAmounts(adjustment.Type, adjustment.Amount, adjustment.Currency,
            rate is { } x ? Money.RoundPkr(adjustment.Amount * x) : null, adjustment.Amount),
    };
}

/// <summary>SPEC §8: the Owner's own net pay plus the final owner earning of every CompanyRecommended and BudgetHire line.</summary>
public static class OwnerIncome
{
    public static decimal Usd(IEnumerable<(HireSource? Source, PayrollLineResult Line)> lines) =>
        lines.Sum(l => l.Source switch
        {
            HireSource.Owner => l.Line.NetPayUsd ?? 0m,
            HireSource.CompanyRecommended or HireSource.BudgetHire => l.Line.OwnerEarningUsd ?? 0m,
            _ => 0m,
        });

    public static decimal Pkr(IEnumerable<(HireSource? Source, PayrollLineResult Line)> lines) =>
        lines.Sum(l => l.Source switch
        {
            HireSource.Owner => l.Line.NetPayPkr ?? 0m,
            HireSource.CompanyRecommended or HireSource.BudgetHire => l.Line.OwnerEarningPkr ?? 0m,
            _ => 0m,
        });
}
