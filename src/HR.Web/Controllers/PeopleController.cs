using System.Security.Claims;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Web.Formatting;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>
/// Employees and internees (SPEC §2). Managers and Admins share every page; the hire source is Admin-only and is
/// loaded through separate service calls only when the user is an Admin.
/// </summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("people")]
public class PeopleController(PersonService people, IClock clock) : Controller
{
    public const string ConflictMessage =
        "Someone else saved changes to this person while you were editing. The form now shows the latest saved values; review the differences below and save again.";

    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    private bool IsAdmin => User.IsInRole(AppRoles.Admin);

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? type, string? status, string? sort, string? hireSource, int page = 1, CancellationToken cancellationToken = default)
    {
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];
        PersonType? typeFilter = Enum.TryParse<PersonType>(type, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : null;
        var statusFilter = Enum.TryParse<PersonStatusFilter>(status, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : PersonStatusFilter.Active;
        var (sortBy, descending, sortValue) = PeopleSortOptions.Parse(sort);
        var query = new PeopleQuery(search, typeFilter, statusFilter, sortBy, descending, page);

        if (IsAdmin)
        {
            var hireFilter = Enum.TryParse<HireSourceFilter>(hireSource, ignoreCase: true, out var h) && Enum.IsDefined(h) ? h : HireSourceFilter.All;
            var result = await people.ListForAdminAsync(query, hireFilter, cancellationToken);
            var rows = result.Items.Select(r => ToRow(r.Person)).ToList();
            var sources = result.Items.ToDictionary(r => r.Person.Id, r => r.HireSource);
            return View(new PeopleListViewModel(rows, search, typeFilter, statusFilter, sortValue, result.Page, result.TotalPages, result.TotalCount,
                new AdminPeopleListViewModel(hireFilter, sources)));
        }

        // Managers: the hire-source parameter is ignored and nothing about hire sources is loaded.
        var managerResult = await people.ListAsync(query, cancellationToken);
        return View(new PeopleListViewModel(managerResult.Items.Select(ToRow).ToList(), search, typeFilter, statusFilter, sortValue,
            managerResult.Page, managerResult.TotalPages, managerResult.TotalCount, Admin: null));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var person = await people.GetAsync(id, cancellationToken);
        if (person is null)
        {
            return NotFound();
        }

        HireSourceCardViewModel? hireCard = null;
        if (IsAdmin)
        {
            var (_, source) = await people.GetHireSourceAsync(id, cancellationToken);
            hireCard = new HireSourceCardViewModel(source);
        }

        var today = clock.Today;
        var minRejoin = person.LeavingDate?.AddDays(1);
        var defaultLeaving = today < person.JoiningDate ? person.JoiningDate : today;
        var defaultRejoin = minRejoin is { } min && min > today ? min : today;
        var history = (await people.GetEmploymentHistoryAsync(id, cancellationToken))
            .Select(span => new EmploymentPeriodRowViewModel(
                span.Start,
                span.End,
                HR.Domain.Payroll.WorkingDays.Count(span.Start, span.End ?? today)))
            .Reverse() // newest first
            .ToList();

        return View(new PersonDetailsViewModel(person, defaultLeaving, minRejoin, defaultRejoin, hireCard, history));
    }

    [HttpGet("create")]
    public IActionResult Create() => View(new PersonFormViewModel { Type = PersonType.Employee, JoiningDate = clock.Today });

    [HttpPost("create")]
    public async Task<IActionResult> Create(PersonFormViewModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(form);
        }

        var result = await people.CreateAsync(form.ToInput(), ActorId, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(form);
        }

        TempData.ToastSuccess($"{form.FullName?.Trim()} has been added.");
        return RedirectToAction(nameof(Details), new { id = result.Id });
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var person = await people.GetAsync(id, cancellationToken);
        return person is null ? NotFound() : View(EditModel(person, PersonFormViewModel.From(person)));
    }

    [HttpPost("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, PersonFormViewModel form, CancellationToken cancellationToken)
    {
        var person = await people.GetAsync(id, cancellationToken);
        if (person is null)
        {
            return NotFound();
        }

        // Blank CNIC/IBAN means "keep what is stored" unless the remove box is ticked.
        var keepCnic = string.IsNullOrWhiteSpace(form.Cnic) && !form.RemoveCnic;
        var keepIban = string.IsNullOrWhiteSpace(form.Iban) && !form.RemoveIban;

        if (!ModelState.IsValid)
        {
            return View(EditModel(person, form));
        }

        byte[] rowVersion;
        try
        {
            rowVersion = Convert.FromBase64String(form.RowVersion ?? string.Empty);
        }
        catch (FormatException)
        {
            return BadRequest();
        }

        var result = await people.UpdateAsync(id, form.ToInput(), keepCnic, keepIban, rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PersonResultStatus.Success:
                TempData.ToastSuccess($"{form.FullName?.Trim()} has been updated.");
                return RedirectToAction(nameof(Details), new { id });
            case PersonResultStatus.NotFound:
                return NotFound();
            case PersonResultStatus.Conflict:
                var current = result.Current!;
                ModelState.Clear();
                ModelState.AddModelError(string.Empty, ConflictMessage);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return View(EditModel(current, PersonFormViewModel.From(current), Differences(form, current, keepCnic, keepIban)));
            default:
                AddErrors(result);
                return View(EditModel(person, form));
        }
    }

    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, DateOnly? leavingDate, CancellationToken cancellationToken)
    {
        var result = await people.DeactivateAsync(id, leavingDate, ActorId, cancellationToken);
        return await AfterStatusChange(id, result, "has been deactivated.", cancellationToken);
    }

    [HttpPost("{id:int}/reactivate")]
    public async Task<IActionResult> Reactivate(int id, DateOnly? rejoiningDate, CancellationToken cancellationToken)
    {
        var result = await people.ReactivateAsync(id, rejoiningDate, ActorId, revealHireSource: IsAdmin, cancellationToken);
        return await AfterStatusChange(id, result, "has been reactivated.", cancellationToken);
    }

    /// <summary>Admin only (SPEC §2). The value is a hire-source name, or empty for "not assigned".</summary>
    [HttpPost("{id:int}/hire-source")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> SetHireSource(int id, string? hireSource, CancellationToken cancellationToken)
    {
        Domain.People.HireSource? source = null;
        if (!string.IsNullOrWhiteSpace(hireSource))
        {
            // Names only: Enum.TryParse would also accept numbers such as "3".
            if (!Enum.GetNames<Domain.People.HireSource>().Contains(hireSource, StringComparer.Ordinal)
                || !Enum.TryParse<Domain.People.HireSource>(hireSource, ignoreCase: false, out var parsed))
            {
                TempData.ToastError("Choose a valid hire source.");
                return RedirectToAction(nameof(Details), new { id });
            }

            source = parsed;
        }

        var result = await people.SetHireSourceAsync(id, source, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PersonResultStatus.NotFound:
                return NotFound();
            case PersonResultStatus.Success:
                TempData.ToastSuccess($"Hire source set to {HireSourceDisplay.Label(source)}.");
                break;
            default:
                TempData.ToastError(FirstError(result));
                break;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<IActionResult> AfterStatusChange(int id, PersonResult result, string successSuffix, CancellationToken cancellationToken)
    {
        if (result.Status == PersonResultStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            var person = await people.GetAsync(id, cancellationToken);
            TempData.ToastSuccess($"{person?.FullName} {successSuffix}");
        }
        else
        {
            TempData.ToastError(FirstError(result));
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private static PersonRowViewModel ToRow(PersonRow r) =>
        new(r.Id, r.Code, r.FullName, r.Designation, r.Type, r.JoiningDate, r.LeavingDate, r.IsActive);

    private static PersonEditViewModel EditModel(PersonDetails person, PersonFormViewModel form, IReadOnlyList<FieldChangeViewModel>? changes = null) =>
        new(person.Id, person.Code, person.IsActive,
            person.Cnic is null ? null : Masking.Cnic(person.Cnic),
            person.Iban is null ? null : Masking.Iban(person.Iban),
            form, changes);

    private static List<FieldChangeViewModel> Differences(PersonFormViewModel yours, PersonDetails saved, bool keepCnic, bool keepIban)
    {
        var mine = yours.ToInput().Normalize(out _) ?? yours.ToInput();
        var changes = new List<FieldChangeViewModel>();

        void Compare(string label, string? savedValue, string? yourValue)
        {
            if (!string.Equals(savedValue ?? string.Empty, yourValue ?? string.Empty, StringComparison.Ordinal))
            {
                changes.Add(new FieldChangeViewModel(label, Show(savedValue), Show(yourValue)));
            }
        }

        static string Show(string? value) => string.IsNullOrEmpty(value) ? "(empty)" : value;

        Compare("Full name", saved.FullName, mine.FullName);
        Compare("Person type", saved.Type.ToString(), mine.Type?.ToString());
        Compare("Designation", saved.Designation, mine.Designation);
        Compare("Email", saved.Email, mine.Email);
        Compare("Phone", PakistaniPhone.Format(saved.Phone), mine.Phone is null ? yours.Phone : PakistaniPhone.Format(mine.Phone));
        if (!keepCnic)
        {
            Compare("CNIC", Masking.Cnic(saved.Cnic), Masking.Cnic(mine.Cnic));
        }

        Compare("Bank name", saved.BankName, mine.BankName);
        if (!keepIban)
        {
            Compare("IBAN", Masking.Iban(saved.Iban), Masking.Iban(mine.Iban));
        }

        Compare("Joining date", DisplayFormat.Date(saved.JoiningDate), mine.JoiningDate is { } j ? DisplayFormat.Date(j) : null);
        Compare("Notes", saved.Notes, mine.Notes);
        return changes;
    }

    private void AddErrors(PersonResult result)
    {
        foreach (var error in result.Errors ?? [new PersonError(string.Empty, "The change could not be saved.")])
        {
            ModelState.AddModelError(error.Field, error.Message);
        }
    }

    private static string FirstError(PersonResult result) =>
        result.Errors is { Count: > 0 } errors ? errors[0].Message : "The change could not be saved. Please try again.";
}
