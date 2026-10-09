using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HR.Domain.Pay;
using HR.Domain.People;
using HR.Infrastructure.Pay;

namespace HR.Web.ViewModels;

/// <summary>The "effective from" picker: a month plus "1st–15th" (1) or "16th–end" (16).</summary>
public abstract class PayPeriodPickerForm
{
    [Required(ErrorMessage = "Choose the month.")]
    [Display(Name = "Month")]
    public string? EffectiveMonth { get; set; }

    [Display(Name = "Pay period")]
    public int EffectiveHalf { get; set; } = 1;

    public DateOnly? EffectiveFrom()
    {
        if (string.IsNullOrWhiteSpace(EffectiveMonth)
            || !DateOnly.TryParseExact(EffectiveMonth + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            return null;
        }

        return EffectiveHalf == 16 ? month.AddDays(15) : month;
    }

    public void SetEffectiveFrom(DateOnly date)
    {
        EffectiveMonth = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        EffectiveHalf = date.Day >= 16 ? 16 : 1;
    }
}

/// <summary>
/// The Admin's pay form. Which amounts count depends on the hire source; the server derives billed, commission and
/// currency itself and ignores fields that don't apply (SPEC §2).
/// </summary>
public sealed class AdminPayFormViewModel : PayPeriodPickerForm
{
    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Enter a positive amount.")]
    public decimal? Salary { get; set; }

    [Range(typeof(decimal), "0", "10000", ErrorMessage = "The commission must be between $0 and $10,000 per pay period.")]
    public decimal? Commission { get; set; }

    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Enter a positive amount.")]
    public decimal? Budget { get; set; }

    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Enter a positive amount.")]
    public decimal? Pay { get; set; }

    public PayCurrency? Currency { get; set; }

    [StringLength(RateRecord.NoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? Note { get; set; }

    /// <summary>Edit only: label this change as a Correction instead of deriving the change type.</summary>
    public bool MarkCorrection { get; set; }

    /// <summary>BudgetHire: "I understand this hire loses money".</summary>
    public bool ConfirmLoss { get; set; }

    public string? RowVersion { get; set; }

    public AdminPayInput ToInput() => new(EffectiveFrom(), Salary, Commission, Budget, Pay, Currency);

    public static AdminPayFormViewModel From(AdminPayRecord record, HireSource source)
    {
        var form = new AdminPayFormViewModel
        {
            Note = record.Note,
            MarkCorrection = record.ChangeType == RateChangeType.Correction,
            RowVersion = Convert.ToBase64String(record.RowVersion),
            Currency = record.Terms.PayCurrency,
        };
        form.SetEffectiveFrom(record.Terms.EffectiveFrom);
        if (source == HireSource.BudgetHire)
        {
            form.Budget = record.Terms.BilledMonthlyUsd;
            form.Pay = record.Terms.PayMonthlyAmount;
        }
        else
        {
            form.Salary = record.Terms.PayMonthlyAmount;
            form.Commission = record.Terms.CommissionPerPeriodUsd;
        }

        return form;
    }
}

/// <param name="LossPayUsd">Set when the server found pay ≥ budget and needs the "loses money" confirmation.</param>
public sealed record AdminPayFormPage(
    int PersonId,
    string PersonName,
    string PersonCode,
    HireSource Source,
    int? RecordId,
    AdminPayFormViewModel Form,
    decimal? UsdToPkr,
    decimal? LossPayUsd,
    DateOnly EarliestEffectiveFrom);

/// <summary>The Manager's increment form: date, new monthly pay and a note. Nothing else is accepted.</summary>
public sealed class IncrementFormViewModel : PayPeriodPickerForm
{
    [Required(ErrorMessage = "Enter the new monthly pay.")]
    [Range(typeof(decimal), "0", "100000000", ErrorMessage = "Enter a positive amount.")]
    [Display(Name = "New monthly pay")]
    public decimal? Pay { get; set; }

    [StringLength(RateRecord.NoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    [Display(Name = "Note (optional)")]
    public string? Note { get; set; }

    public string? RowVersion { get; set; }
}

public sealed record IncrementFormPage(
    int PersonId,
    string PersonName,
    string PersonCode,
    int? RecordId,
    PayCurrency Currency,
    ManagerPayCurrent? Current,
    IncrementFormViewModel Form,
    DateOnly EarliestEffectiveFrom);

/// <summary>Admin pay tab on person details.</summary>
public sealed record AdminPayTabViewModel(int PersonId, AdminPayTab Tab, DateOnly Today);

/// <summary>Manager pay tab on person details: pay side only.</summary>
public sealed record ManagerPayTabViewModel(int PersonId, ManagerPayTab Tab);

public sealed record SalariesPageViewModel(
    IReadOnlyList<SalaryRow> Rows,
    string? Search,
    SalaryFilter Filter,
    string Sort,
    int Page,
    int TotalPages,
    int TotalCount,
    AdminSalariesViewModel? Admin)
{
    public Dictionary<string, string> RouteFor(int page)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["page"] = page.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            values["q"] = Search;
        }

        if (Filter != SalaryFilter.All)
        {
            values["filter"] = Filter.ToString();
        }

        if (Sort != SalarySortOptions.Default)
        {
            values["sort"] = Sort;
        }

        return values;
    }
}

/// <summary>Admin-only salaries data. Null for Managers.</summary>
public sealed record AdminSalariesViewModel(IReadOnlyDictionary<int, AdminSalaryRow> Rows, AdminSalaryTotals Totals);

public static class SalarySortOptions
{
    public const string Default = "name";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("name", "Name (A–Z)"),
        ("name_desc", "Name (Z–A)"),
        ("code", "Code"),
        ("since_desc", "Pay since (newest)"),
        ("since", "Pay since (oldest)"),
    ];

    public static (SalarySort Sort, bool Descending, string Value) Parse(string? value) => value switch
    {
        "name_desc" => (SalarySort.Name, true, "name_desc"),
        "code" => (SalarySort.Code, false, "code"),
        "since_desc" => (SalarySort.Since, true, "since_desc"),
        "since" => (SalarySort.Since, false, "since"),
        _ => (SalarySort.Name, false, Default),
    };
}
