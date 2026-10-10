using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Infrastructure.Payroll;
using HR.Web.Formatting;

namespace HR.Web.ViewModels;

/// <summary>Shared wording for payroll pages. Nothing here mentions billing; Admin-only text lives in the Admin partials.</summary>
public static class PayrollDisplay
{
    public static string Days(decimal days) => days.ToString("0.##", CultureInfo.InvariantCulture);

    public static string StatusPill(PayrollStatus status) => status == PayrollStatus.Finalized ? "pill-success" : "pill-warning";

    public static string StatusLabel(PayrollStatus status) => status == PayrollStatus.Finalized ? "Finalized" : "Draft";

    /// <summary>"9 of 11" or "10.5 of 11 incl. 1.5 extra".</summary>
    public static string PayableDays(decimal payable, int working, decimal extra) =>
        extra == 0m ? $"{Days(payable)} of {working}" : $"{Days(payable)} of {working} incl. {Days(extra)} extra";

    /// <summary>The fraction used in breakdowns, e.g. "10.5/11".</summary>
    public static string Fraction(decimal payable, int working) => $"{Days(payable)}/{working}";

    public static string Rate(decimal rate) => "Rs " + rate.ToString("#,##0.00##", CultureInfo.InvariantCulture);

    /// <summary>What a Manager sees for any line problem; the Admin text names the hire source.</summary>
    public static string IssueText(LineIssue issue, bool isAdmin) => issue switch
    {
        LineIssue.NoHireSource => isAdmin ? "No hire source" : "Pay setup incomplete",
        LineIssue.NoRateRecordAtPeriodStart => isAdmin ? "No pay record in effect on the period's first day" : "Pay setup incomplete",
        LineIssue.NegativeNetPay => "Deductions exceed pay",
        _ => issue.ToString(),
    };

    public static string IssueHint(LineIssue issue, bool isAdmin) => issue switch
    {
        LineIssue.NoHireSource => isAdmin ? "Set the hire source and pay on the person's page, then regenerate." : "Ask the administrator to complete this person's pay setup, then regenerate.",
        LineIssue.NoRateRecordAtPeriodStart => isAdmin ? "Add a pay record starting on or before the period's first day, then regenerate." : "Ask the administrator to complete this person's pay setup, then regenerate.",
        LineIssue.NegativeNetPay => "Reduce or remove a deduction on this line so net pay is not below zero.",
        _ => string.Empty,
    };

    public static string AdjustmentLabel(AdjustmentType type) => type switch
    {
        AdjustmentType.Bonus => "Bonus",
        AdjustmentType.Reimbursement => "Reimbursement",
        _ => "Deduction",
    };

    public static string Signed(decimal? amount, PayCurrency currency, int sign) =>
        amount is { } a ? (sign < 0 ? "− " : "+ ") + DisplayFormat.Money(a, currency) : "—";

    public static string Pkr(decimal? amount) => amount is { } a ? DisplayFormat.Pkr(a) : "—";

    public static string Usd(decimal? amount) => amount is { } a ? DisplayFormat.Usd(a) : "—";

    public static string EventLabel(PayrollEventType type) => type switch
    {
        PayrollEventType.Generated => "Generated",
        PayrollEventType.Regenerated => "Recalculated",
        PayrollEventType.RateChanged => "Exchange rate changed",
        PayrollEventType.Finalized => "Finalized",
        _ => "Reopened",
    };

    /// <summary>Base pay in plain language, e.g. "Rs 196,000 ÷ 2 × 9/11 = Rs 80,182".</summary>
    public static IReadOnlyList<string> PayBreakdown(PayrollLineRow l, decimal? rate)
    {
        if (l.PayMonthlyAmount is not { } monthly || l.PayCurrency is not { } currency)
        {
            return [];
        }

        var fraction = Fraction(l.PayableDays, l.WorkingDays);
        var lines = new List<string>();
        if (currency == PayCurrency.PKR)
        {
            lines.Add($"{DisplayFormat.Pkr(monthly)} ÷ 2 × {fraction} = {Pkr(l.PayPkr)}");
            if (rate is { } r && l.PayUsd is { } usd)
            {
                lines.Add($"{Pkr(l.PayPkr)} ÷ {r.ToString("0.00##", CultureInfo.InvariantCulture)} = {DisplayFormat.Usd(usd)} (reference)");
            }
        }
        else
        {
            lines.Add($"{DisplayFormat.Usd(monthly)} ÷ 2 × {fraction} = {Usd(l.PayUsd)}");
            if (rate is { } r && l.PayPkr is { } pkr)
            {
                lines.Add($"{Usd(l.PayUsd)} × {r.ToString("0.00##", CultureInfo.InvariantCulture)} = {DisplayFormat.Pkr(pkr)}");
            }
        }

        return lines;
    }
}

// ---------- List and generate ----------

public sealed record PayrollIndexViewModel(
    IReadOnlyList<PayrollRunRow> Runs,
    IReadOnlyDictionary<int, PayrollAdminTotals>? AdminTotals,
    GeneratePayrollForm Generate);

public sealed class GeneratePayrollForm
{
    [Required(ErrorMessage = "Choose the month.")]
    public string? Month { get; set; }

    /// <summary>1 for the 1st–15th, 16 for the 16th–month end.</summary>
    public int Half { get; set; } = 1;

    public DateOnly? PeriodStart() =>
        DateOnly.TryParseExact((Month ?? string.Empty) + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            ? (Half == 16 ? month.AddDays(15) : month)
            : null;

    public static GeneratePayrollForm For(PayPeriod period) => new()
    {
        Month = period.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        Half = period.IsFirstHalf ? 1 : 16,
    };
}

// ---------- Run page ----------

public sealed record PayrollRunViewModel(
    PayrollRunHeader Run,
    IReadOnlyList<PayrollLineRow> Lines,
    IReadOnlyDictionary<int, PayrollLineBilling>? Billing,
    IReadOnlyList<PayrollEventRow> Events,
    IReadOnlyList<LineChange>? Changes,
    decimal? HistoryRate,
    DateOnly? HistoryRateDate,
    RateForm RateForm,
    HR.Infrastructure.Invoices.RunInvoice? Invoice = null)
{
    public bool IsAdmin => Billing is not null;

    public IEnumerable<PayrollLineRow> Included => Lines.Where(l => !l.IsOrphaned);

    public IEnumerable<PayrollLineRow> Orphans => Lines.Where(l => l.IsOrphaned);

    public IEnumerable<PayrollLineRow> Blocked => Included.Where(l => l.Issue is not null);

    public decimal Sum(Func<PayrollLineRow, decimal?> value) => Included.Sum(l => value(l) ?? 0m);

    public decimal SumBilling(Func<PayrollLineBilling, decimal?> value) =>
        Billing is null ? 0m : Included.Sum(l => Billing.TryGetValue(l.Id, out var b) ? value(b) ?? 0m : 0m);
}

public sealed class RateForm
{
    /// <summary>"history" uses the rate in effect on the period's last day; anything else overrides with <see cref="Rate"/>.</summary>
    public string? Source { get; set; } = "history";

    public decimal? Rate { get; set; }

    [StringLength(PayrollRun.RateNoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? RateNote { get; set; }
}

public sealed class ReopenForm
{
    [Required(ErrorMessage = "Give the reason for reopening.")]
    [StringLength(PayrollRun.ReopenReasonMaxLength, MinimumLength = PayrollRun.ReopenReasonMinLength, ErrorMessage = "The reason must be 10 to 500 characters.")]
    public string? Reason { get; set; }
}

// ---------- Line page ----------

public sealed record PayrollLineViewModel(
    PayrollRunHeader Run,
    PayrollLineDetail Detail,
    PayrollLineBilling? Billing,
    ExtraDaysForm ExtraDays,
    AdjustmentForm Adjustment)
{
    public PayrollLineRow Line => Detail.Line;

    public bool IsAdmin => Billing is not null;
}

public sealed class ExtraDaysForm
{
    public decimal? ExtraDays { get; set; }

    [StringLength(PayrollLine.ExtraDaysNoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? ExtraDaysNote { get; set; }
}

public sealed class AdjustmentForm
{
    [Required(ErrorMessage = "Choose the type.")]
    public string? Type { get; set; } = nameof(AdjustmentType.Bonus);

    [Required(ErrorMessage = "Enter the amount.")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Choose the currency.")]
    public string? Currency { get; set; } = nameof(PayCurrency.PKR);

    [StringLength(AdjustmentRules.NoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? Note { get; set; }

    public string? RowVersion { get; set; }

    public AdjustmentType? ParsedType() =>
        Enum.GetNames<AdjustmentType>().Contains(Type, StringComparer.Ordinal) ? Enum.Parse<AdjustmentType>(Type!) : null;

    public PayCurrency? ParsedCurrency() =>
        Enum.GetNames<PayCurrency>().Contains(Currency, StringComparer.Ordinal) ? Enum.Parse<PayCurrency>(Currency!) : null;
}

public sealed record AdjustmentEditViewModel(PayrollRunHeader Run, PayrollLineRow Line, int AdjustmentId, AdjustmentForm Form);

// ---------- Payslip and register ----------

public sealed record PayslipViewModel(PayrollRunHeader Run, PayrollLineDetail Detail, string IssuerName);

public sealed record PayslipsViewModel(PayrollRunHeader Run, IReadOnlyList<PayrollLineDetail> Lines, string IssuerName);

public sealed record RegisterViewModel(PayrollRunHeader Run, IReadOnlyList<RegisterRow> Rows, string IssuerName)
{
    public decimal Total => Rows.Sum(r => r.NetPayPkr ?? 0m);
}
