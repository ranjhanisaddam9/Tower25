using System.ComponentModel.DataAnnotations;
using System.Globalization;
using HR.Domain.Absences;
using HR.Domain.Payroll;
using HR.Infrastructure;
using HR.Infrastructure.Absences;
using HR.Infrastructure.People;

namespace HR.Web.ViewModels;

/// <summary>Shared wording for paid/unpaid parts, so every page says it the same way (text, not only colour).</summary>
public static class AbsenceDisplay
{
    public static string Days(decimal days) => days.ToString("0.##", CultureInfo.InvariantCulture);

    public static string DaysWithUnit(decimal days) => Days(days) + (days == 1m ? " day" : " days");

    public static string PortionLabel(AbsencePortion portion) => portion == AbsencePortion.Full ? "Full day" : "Half day";

    /// <summary>"Paid", "Unpaid" or "Partly paid".</summary>
    public static string PaidLabel(decimal paid, decimal unpaid) => unpaid == 0m ? "Paid" : paid == 0m ? "Unpaid" : "Partly paid";

    public static string PaidPill(decimal paid, decimal unpaid) => unpaid == 0m ? "pill-success" : paid == 0m ? "pill-danger" : "pill-warning";

    public static string PaidIcon(decimal paid, decimal unpaid) => unpaid == 0m ? "bi-check-circle" : paid == 0m ? "bi-x-circle" : "bi-circle-half";

    /// <summary>"0.5 paid · 0.5 unpaid" style detail.</summary>
    public static string Split(decimal paid, decimal unpaid) => $"{Days(paid)} paid · {Days(unpaid)} unpaid";

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Weekday(DateOnly date) => date.ToString("ddd", CultureInfo.InvariantCulture);

    /// <summary>The note shown after a change that moved paid leave between later absences.</summary>
    public static string? LaterChangedNote(int count, DateOnly date) => count switch
    {
        0 => null,
        1 => $"This changed the paid/unpaid status of 1 later absence in {AbsenceService.MonthLabel(date)}.",
        _ => $"This changed the paid/unpaid status of {count} later absences in {AbsenceService.MonthLabel(date)}.",
    };

    public static string? WouldChangeNote(int count, DateOnly date, string action) => count switch
    {
        0 => null,
        1 => $"{action} will change the paid/unpaid status of 1 later absence in {AbsenceService.MonthLabel(date)}.",
        _ => $"{action} will change the paid/unpaid status of {count} later absences in {AbsenceService.MonthLabel(date)}.",
    };

    public static string DeleteConfirm(DateOnly date, int laterChanged) =>
        $"The absence on {HR.Web.Formatting.DisplayFormat.Date(date)} will be removed." +
        (WouldChangeNote(laterChanged, date, "Deleting it") is { } note ? " " + note : string.Empty);

    public static string OutcomeLabel(RangeOutcome outcome) => outcome switch
    {
        RangeOutcome.WillAdd => "Will add",
        RangeOutcome.Weekend => "Skipped: weekend",
        RangeOutcome.AlreadyRecorded => "Skipped: already recorded",
        RangeOutcome.NotEmployed => "Skipped: not employed",
        RangeOutcome.Locked => "Skipped: locked",
        RangeOutcome.TooFarAhead => "Skipped: more than a year ahead",
        _ => outcome.ToString(),
    };
}

// ---------- List ----------

public sealed record AbsenceListViewModel(
    AbsenceList Result,
    DateOnly Today,
    string? Search,
    AbsencePortion? Portion,
    PaidStatusFilter Paid,
    PersonStatusFilter Status)
{
    public PayPeriod Period => Result.Period;

    public PagedResult<AbsenceRow> Rows => Result.Rows;

    public bool IsCurrentPeriod => Period.Contains(Today);

    public bool HasFilter => !string.IsNullOrWhiteSpace(Search) || Portion is not null || Paid != PaidStatusFilter.All || Status != PersonStatusFilter.Active;

    public Dictionary<string, string> RouteFor(int page, PayPeriod? period = null)
    {
        var route = new Dictionary<string, string> { ["period"] = AbsenceDisplay.Iso((period ?? Period).Start) };
        if (!string.IsNullOrWhiteSpace(Search)) route["q"] = Search;
        if (Portion is { } portion) route["portion"] = portion.ToString();
        if (Paid != PaidStatusFilter.All) route["paid"] = Paid.ToString();
        if (Status != PersonStatusFilter.Active) route["status"] = Status.ToString();
        if (page > 1) route["page"] = page.ToString(CultureInfo.InvariantCulture);
        return route;
    }
}

// ---------- Single add / edit ----------

public sealed class AbsenceFormViewModel
{
    [Required(ErrorMessage = "Choose a person.")]
    [Display(Name = "Person")]
    public int? PersonId { get; set; }

    [Required(ErrorMessage = "Enter the date.")]
    [DataType(DataType.Date)]
    public DateOnly? Date { get; set; }

    [Required(ErrorMessage = "Choose full or half day.")]
    public string? Portion { get; set; } = nameof(AbsencePortion.Full);

    [StringLength(Absence.NoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? Note { get; set; }

    public string? RowVersion { get; set; }

    /// <summary>"person" returns to the person's Absences tab; anything else to the list.</summary>
    public string? ReturnTo { get; set; }
}

public sealed record PersonOption(int Id, string Code, string FullName, bool IsActive);

public sealed record AbsenceFormPage(
    AbsenceFormViewModel Form,
    IReadOnlyList<PersonOption> People,
    AbsenceDetails? Existing,
    DateOnly LatestAllowed)
{
    public bool IsEdit => Existing is not null;
}

// ---------- Daily attendance ----------

public sealed class AttendanceRowForm
{
    public int PersonId { get; set; }

    /// <summary>Present, Half or Full.</summary>
    public string? Status { get; set; }

    [StringLength(Absence.NoteMaxLength, ErrorMessage = "A note can be at most 300 characters.")]
    public string? Note { get; set; }
}

public sealed class AttendanceForm
{
    public DateOnly? Date { get; set; }

    public List<AttendanceRowForm> Rows { get; set; } = [];
}

public sealed record AttendancePage(
    DateOnly Date,
    DateOnly Today,
    bool Weekend,
    bool Locked,
    IReadOnlyList<AttendanceRow> Rows,
    AttendanceForm? Posted)
{
    public const string Present = "Present";

    public static DateOnly PreviousWorkingDay(DateOnly date)
    {
        var d = date.AddDays(-1);
        while (!WorkingDays.IsWorkingDay(d)) d = d.AddDays(-1);
        return d;
    }

    public static DateOnly NextWorkingDay(DateOnly date)
    {
        var d = date.AddDays(1);
        while (!WorkingDays.IsWorkingDay(d)) d = d.AddDays(1);
        return d;
    }

    /// <summary>The status to show: what was posted (after a failed save), else what is stored.</summary>
    public string StatusFor(AttendanceRow row) =>
        Posted?.Rows.FirstOrDefault(r => r.PersonId == row.PersonId)?.Status ?? row.Portion?.ToString() ?? Present;

    public string? NoteFor(AttendanceRow row) =>
        Posted is { } posted ? posted.Rows.FirstOrDefault(r => r.PersonId == row.PersonId)?.Note : row.Note;

    public int AbsentCount => Rows.Count(r => r.Portion is not null);
}

// ---------- Range ----------

public sealed class RangeForm
{
    public int? PersonId { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? From { get; set; }

    [DataType(DataType.Date)]
    public DateOnly? To { get; set; }

    [StringLength(Absence.NoteMaxLength, ErrorMessage = "The note can be at most 300 characters.")]
    public string? Note { get; set; }

    /// <summary>Confirm step: the dates the preview said it will add. Re-validated on the server.</summary>
    public List<DateOnly> Dates { get; set; } = [];
}

public sealed record RangePage(RangeForm Form, IReadOnlyList<PersonOption> People, RangePreview? Preview);

// ---------- Person details tab ----------

public sealed record PersonAbsenceTabViewModel(PersonAbsenceTab Tab, DateOnly Today)
{
    public DateOnly PreviousMonth => Tab.Month.AddMonths(-1);

    public DateOnly NextMonth => Tab.Month.AddMonths(1);

    public bool IsCurrentMonth => Tab.Month.Year == Today.Year && Tab.Month.Month == Today.Month;

    public string MonthParam(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
