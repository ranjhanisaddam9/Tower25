using System.Security.Claims;
using HR.Domain.Absences;
using HR.Domain.Payroll;
using HR.Domain.Time;
using HR.Infrastructure.Absences;
using HR.Infrastructure.People;
using HR.Web.Formatting;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>
/// Absences and paid leave (SPEC §4), for Managers and Admins alike: nothing here is Admin-only. Paid/unpaid is always
/// computed by the service; the forms only ever post a date, a portion and a note.
/// </summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("absences")]
public class AbsencesController(AbsenceService absences, IClock clock) : Controller
{
    public const string WeekendDayMessage = "That date is a weekend. Attendance is only kept for working days (Monday to Friday).";

    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    // ===================== List =====================

    [HttpGet("")]
    public async Task<IActionResult> Index(DateOnly? period, string? q, string? portion, string? paid, string? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var start = PayPeriod.For(period ?? today).Start;
        AbsencePortion? portionFilter = AbsencePortions.TryParse(portion, out var p) ? p : null;
        var paidFilter = ParseEnum(paid, PaidStatusFilter.All);
        var statusFilter = ParseEnum(status, PersonStatusFilter.Active);
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];

        var result = await absences.ListAsync(new AbsenceQuery(start, search, portionFilter, paidFilter, statusFilter, page), cancellationToken);
        return View(new AbsenceListViewModel(result, today, search, portionFilter, paidFilter, statusFilter));
    }

    // ===================== Single add / edit / delete =====================

    [HttpGet("new")]
    public async Task<IActionResult> New(int? personId, DateOnly? date, string? returnTo, CancellationToken cancellationToken)
    {
        var form = new AbsenceFormViewModel { PersonId = personId, Date = date ?? clock.Today, ReturnTo = returnTo };
        return View("Form", await FormPageAsync(form, null, cancellationToken));
    }

    [HttpPost("new")]
    public async Task<IActionResult> New(AbsenceFormViewModel form, CancellationToken cancellationToken)
    {
        if (!AbsencePortions.TryParse(form.Portion, out var portion))
        {
            ModelState.AddModelError(nameof(form.Portion), "Choose full or half day.");
        }

        if (!ModelState.IsValid)
        {
            return View("Form", await FormPageAsync(form, null, cancellationToken));
        }

        var result = await absences.CreateAsync(form.PersonId!.Value, form.Date!.Value, portion, form.Note, ActorId, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View("Form", await FormPageAsync(form, null, cancellationToken));
        }

        TempData.ToastSuccess(WithNote($"Absence on {DisplayFormat.Date(form.Date.Value)} added.", result.LaterChanged, form.Date.Value));
        return ReturnAfterChange(form.ReturnTo, form.PersonId.Value, form.Date.Value);
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, string? returnTo, CancellationToken cancellationToken)
    {
        var existing = await absences.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        var form = new AbsenceFormViewModel
        {
            PersonId = existing.PersonId,
            Date = existing.Date,
            Portion = existing.Portion.ToString(),
            Note = existing.Note,
            RowVersion = Convert.ToBase64String(existing.RowVersion),
            ReturnTo = returnTo,
        };
        return View("Form", await FormPageAsync(form, existing, cancellationToken));
    }

    [HttpPost("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, AbsenceFormViewModel form, CancellationToken cancellationToken)
    {
        var existing = await absences.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        // The person and date are fixed on edit; whatever was posted for them is ignored.
        form.PersonId = existing.PersonId;
        form.Date = existing.Date;
        ModelState.Remove(nameof(form.PersonId));
        ModelState.Remove(nameof(form.Date));
        if (!AbsencePortions.TryParse(form.Portion, out var portion))
        {
            ModelState.AddModelError(nameof(form.Portion), "Choose full or half day.");
        }

        if (!ModelState.IsValid)
        {
            return View("Form", await FormPageAsync(form, existing, cancellationToken));
        }

        if (!TryRowVersion(form.RowVersion, out var rowVersion))
        {
            return BadRequest();
        }

        var result = await absences.UpdateAsync(id, portion, form.Note, rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case AbsenceResultStatus.Success:
                TempData.ToastSuccess(WithNote($"Absence on {DisplayFormat.Date(existing.Date)} updated.", result.LaterChanged, existing.Date));
                return ReturnAfterChange(form.ReturnTo, existing.PersonId, existing.Date);
            case AbsenceResultStatus.NotFound:
                return NotFound();
            case AbsenceResultStatus.Locked:
                TempData.ToastError(AbsenceRules.LockedMessage);
                return ReturnAfterChange(form.ReturnTo, existing.PersonId, existing.Date);
            default:
                AddErrors(result);
                return View("Form", await FormPageAsync(form, existing, cancellationToken));
        }
    }

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, string? returnTo, CancellationToken cancellationToken)
    {
        var existing = await absences.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        var result = await absences.DeleteAsync(id, ActorId, cancellationToken);
        switch (result.Status)
        {
            case AbsenceResultStatus.NotFound:
                return NotFound();
            case AbsenceResultStatus.Success:
                TempData.ToastSuccess(WithNote($"Absence on {DisplayFormat.Date(existing.Date)} deleted.", result.LaterChanged, existing.Date));
                break;
            default:
                TempData.ToastError(result.Errors?.FirstOrDefault()?.Message ?? AbsenceRules.LockedMessage);
                break;
        }

        return ReturnAfterChange(returnTo, existing.PersonId, existing.Date);
    }

    // ===================== Daily attendance =====================

    [HttpGet("day")]
    public async Task<IActionResult> Day(DateOnly? date, CancellationToken cancellationToken)
    {
        var day = date ?? clock.Today;
        return View(await DayPageAsync(day, null, cancellationToken));
    }

    [HttpPost("day")]
    public async Task<IActionResult> Day(AttendanceForm form, CancellationToken cancellationToken)
    {
        if (form.Date is not { } date)
        {
            return BadRequest();
        }

        var entries = new List<AttendanceEntry>();
        foreach (var row in form.Rows)
        {
            if (string.IsNullOrEmpty(row.Status) || row.Status == AttendancePage.Present)
            {
                entries.Add(new AttendanceEntry(row.PersonId, null, null));
            }
            else if (AbsencePortions.TryParse(row.Status, out var portion))
            {
                entries.Add(new AttendanceEntry(row.PersonId, portion, row.Note));
            }
            else
            {
                ModelState.AddModelError(string.Empty, "Choose Present, Half or Full for every person.");
            }
        }

        if (!ModelState.IsValid)
        {
            return View(await DayPageAsync(date, form, cancellationToken));
        }

        var result = await absences.SaveDayAsync(date, entries, ActorId, cancellationToken);
        if (result.Succeeded)
        {
            TempData.ToastSuccess(result.Added + result.Changed + result.Removed == 0
                ? $"No changes for {DisplayFormat.Date(date)}."
                : $"Attendance for {DisplayFormat.Date(date)} saved: {result.Added} added, {result.Changed} changed, {result.Removed} removed.");
            return RedirectToAction(nameof(Day), new { date = AbsenceDisplay.Iso(date) });
        }

        if (result.Status == AbsenceResultStatus.Locked)
        {
            TempData.ToastError(AbsenceRules.LockedMessage);
            return RedirectToAction(nameof(Day), new { date = AbsenceDisplay.Iso(date) });
        }

        // The sheet has no per-field slots: every error goes to the summary.
        foreach (var error in result.Errors ?? [])
        {
            ModelState.AddModelError(string.Empty, error.Message);
        }

        return View(await DayPageAsync(date, form, cancellationToken));
    }

    // ===================== Range =====================

    /// <summary>Step 1: the form, and the preview once a person and both dates are given. Previewing changes nothing.</summary>
    [HttpGet("range")]
    public async Task<IActionResult> Range(RangeForm form, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        RangePreview? preview = null;
        if (form.PersonId is { } personId && form.From is { } from && form.To is { } to)
        {
            if (form.Note is { Length: > Absence.NoteMaxLength })
            {
                ModelState.AddModelError(nameof(form.Note), AbsenceRules.NoteTooLongMessage);
            }
            else
            {
                var (result, error) = await absences.PreviewRangeAsync(personId, from, to, cancellationToken);
                preview = result;
                if (error is not null)
                {
                    AddErrors(error);
                }
            }
        }
        else
        {
            form.From ??= clock.Today;
            form.To ??= form.From;
        }

        return View(new RangePage(form, await PeopleAsync(cancellationToken), preview));
    }

    /// <summary>Step 2: saves the "will add" dates after checking every one of them again.</summary>
    [HttpPost("range")]
    [ActionName(nameof(Range))]
    public async Task<IActionResult> ConfirmRange(RangeForm form, CancellationToken cancellationToken)
    {
        if (form.PersonId is not { } personId || form.From is not { } from || form.To is not { } to || !ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Choose a person and both dates, then preview again.");
            return View(new RangePage(form, await PeopleAsync(cancellationToken), null));
        }

        var result = await absences.ConfirmRangeAsync(personId, from, to, form.Dates, form.Note, ActorId, cancellationToken);
        if (result.Succeeded)
        {
            TempData.ToastSuccess($"{result.Added} {(result.Added == 1 ? "absence" : "absences")} added.");
            return RedirectToAction(nameof(PeopleController.Details), "People", new { id = personId, tab = "absences", month = from.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) }, "absences");
        }

        AddErrors(result);
        var (preview, _) = await absences.PreviewRangeAsync(personId, from, to, cancellationToken);
        return View(new RangePage(form, await PeopleAsync(cancellationToken), preview));
    }

    // ===================== Helpers =====================

    private async Task<AttendancePage> DayPageAsync(DateOnly date, AttendanceForm? posted, CancellationToken cancellationToken)
    {
        var sheet = await absences.GetDayAsync(date, cancellationToken);
        return new AttendancePage(date, clock.Today, sheet is null, sheet?.Locked ?? false, sheet?.Rows ?? [], posted);
    }

    private async Task<AbsenceFormPage> FormPageAsync(AbsenceFormViewModel form, AbsenceDetails? existing, CancellationToken cancellationToken) =>
        new(form, existing is null ? await PeopleAsync(cancellationToken) : [], existing, AbsenceRules.LatestAllowed(clock.Today));

    private async Task<IReadOnlyList<PersonOption>> PeopleAsync(CancellationToken cancellationToken) =>
        (await absences.PeopleForPickerAsync(cancellationToken)).Select(p => new PersonOption(p.Id, p.Code, p.FullName, p.IsActive)).ToList();

    private IActionResult ReturnAfterChange(string? returnTo, int personId, DateOnly date) =>
        returnTo == "person"
            ? RedirectToAction(nameof(PeopleController.Details), "People", new { id = personId, tab = "absences", month = date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) }, "absences")
            : RedirectToAction(nameof(Index), new { period = AbsenceDisplay.Iso(PayPeriod.For(date).Start) });

    private static string WithNote(string message, int laterChanged, DateOnly date) =>
        AbsenceDisplay.LaterChangedNote(laterChanged, date) is { } note ? $"{message} {note}" : message;

    private void AddErrors(AbsenceResult result)
    {
        foreach (var error in result.Errors ?? [new AbsenceError(string.Empty, "The absence could not be saved.")])
        {
            ModelState.AddModelError(error.Field, error.Message);
        }
    }

    private static T ParseEnum<T>(string? value, T fallback)
        where T : struct, Enum =>
        Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)) is { } name ? Enum.Parse<T>(name) : fallback;

    private static bool TryRowVersion(string? value, out byte[] rowVersion)
    {
        try
        {
            rowVersion = Convert.FromBase64String(value ?? string.Empty);
            return true;
        }
        catch (FormatException)
        {
            rowVersion = [];
            return false;
        }
    }
}
