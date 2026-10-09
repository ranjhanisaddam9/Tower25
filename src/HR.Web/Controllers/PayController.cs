using System.Security.Claims;
using HR.Domain.Pay;
using HR.Domain.People;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.People;
using HR.Web.Formatting;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>
/// Rate records (SPEC §2). Admins set up pay and edit any record (billing included); Managers record increments and
/// edit or delete only records they created, and only ever see the pay side.
/// </summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("people/{personId:int}/pay")]
public class PayController(PayRecordService pay, PersonService people) : Controller
{
    public const string ConflictMessage = "Someone else changed this pay record while you were editing. Reload the record and try again.";
    public const string LossConfirmMessage = "Tick \"I understand this hire loses money\" to save pay that is at or above the budget.";

    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    private bool IsAdmin => User.IsInRole(AppRoles.Admin);

    // ===================== Admin: set up and correct =====================

    [HttpGet("new")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> New(int personId, CancellationToken cancellationToken)
    {
        var (person, tab) = await LoadAdminAsync(personId, cancellationToken);
        if (person is null || tab is null)
        {
            return NotFound();
        }

        if (tab.Source is not { } source)
        {
            TempData.ToastError("Set this person's hire source before setting up pay.");
            return RedirectToDetails(personId);
        }

        var form = new AdminPayFormViewModel
        {
            Commission = source == HireSource.CompanyRecommended ? tab.DefaultCommission : null,
            Currency = source == HireSource.BudgetHire ? PayCurrency.PKR : PayCurrency.USD,
        };
        form.SetEffectiveFrom(tab.DefaultEffectiveFrom);
        return View("AdminForm", new AdminPayFormPage(personId, person.FullName, person.Code, source, null, form, tab.UsdToPkr, null, tab.EarliestEffectiveFrom));
    }

    [HttpPost("new")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> New(int personId, AdminPayFormViewModel form, CancellationToken cancellationToken)
    {
        var (person, tab) = await LoadAdminAsync(personId, cancellationToken);
        if (person is null || tab is null)
        {
            return NotFound();
        }

        if (tab.Source is not { } source)
        {
            TempData.ToastError("Set this person's hire source before setting up pay.");
            return RedirectToDetails(personId);
        }

        var page = new AdminPayFormPage(personId, person.FullName, person.Code, source, null, form, tab.UsdToPkr, null, tab.EarliestEffectiveFrom);
        if (!ModelState.IsValid)
        {
            return View("AdminForm", page);
        }

        var result = await pay.CreateAdminAsync(personId, form.ToInput(), form.Note, form.ConfirmLoss, ActorId, cancellationToken);
        if (result.Succeeded)
        {
            TempData.ToastSuccess("Pay record saved.");
            return RedirectToDetails(personId);
        }

        return View("AdminForm", Failed(page, result));
    }

    [HttpGet("{recordId:int}/edit")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Edit(int personId, int recordId, CancellationToken cancellationToken)
    {
        var (person, tab) = await LoadAdminAsync(personId, cancellationToken);
        var record = await pay.GetAdminRecordAsync(personId, recordId, cancellationToken);
        if (person is null || tab?.Source is not { } source || record is null)
        {
            return NotFound();
        }

        return View("AdminForm", new AdminPayFormPage(personId, person.FullName, person.Code, source, recordId,
            AdminPayFormViewModel.From(record, source), tab.UsdToPkr, null, tab.EarliestEffectiveFrom));
    }

    [HttpPost("{recordId:int}/edit")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Edit(int personId, int recordId, AdminPayFormViewModel form, CancellationToken cancellationToken)
    {
        var (person, tab) = await LoadAdminAsync(personId, cancellationToken);
        if (person is null || tab?.Source is not { } source || await pay.GetAdminRecordAsync(personId, recordId, cancellationToken) is null)
        {
            return NotFound();
        }

        var page = new AdminPayFormPage(personId, person.FullName, person.Code, source, recordId, form, tab.UsdToPkr, null, tab.EarliestEffectiveFrom);
        if (!ModelState.IsValid)
        {
            return View("AdminForm", page);
        }

        if (!TryRowVersion(form.RowVersion, out var rowVersion))
        {
            return BadRequest();
        }

        var result = await pay.UpdateAdminAsync(personId, recordId, form.ToInput(), form.Note, form.MarkCorrection, form.ConfirmLoss, rowVersion, ActorId, cancellationToken);
        if (result.Succeeded)
        {
            TempData.ToastSuccess("Pay record updated.");
            return RedirectToDetails(personId);
        }

        return result.Status switch
        {
            PayResultStatus.NotFound => NotFound(),
            PayResultStatus.Locked => LockedRedirect(personId, result),
            _ => View("AdminForm", Failed(page, result)),
        };
    }

    [HttpPost("{recordId:int}/reviewed")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Reviewed(int personId, int recordId, CancellationToken cancellationToken)
    {
        var result = await pay.MarkReviewedAsync(personId, recordId, ActorId, cancellationToken);
        if (result.Status == PayResultStatus.NotFound)
        {
            return NotFound();
        }

        TempData.ToastSuccess("Marked as reviewed.");
        return RedirectToDetails(personId);
    }

    /// <summary>Admins may delete any record; Managers only their own (the service enforces it).</summary>
    [HttpPost("{recordId:int}/delete")]
    public async Task<IActionResult> Delete(int personId, int recordId, CancellationToken cancellationToken)
    {
        var result = await pay.DeleteAsync(personId, recordId, ActorId, IsAdmin, cancellationToken);
        switch (result.Status)
        {
            case PayResultStatus.NotFound:
                return NotFound();
            case PayResultStatus.Forbidden:
                return Forbid();
            case PayResultStatus.Locked:
                return LockedRedirect(personId, result);
            default:
                TempData.ToastSuccess("Pay record deleted.");
                return RedirectToDetails(personId);
        }
    }

    // ===================== Increments (Managers; Admins may use them too) =====================

    [HttpGet("increment")]
    public async Task<IActionResult> Increment(int personId, CancellationToken cancellationToken)
    {
        var page = await IncrementPageAsync(personId, null, null, cancellationToken);
        if (page is null)
        {
            return NotFound();
        }

        return page.Value.Page is { } p ? View("IncrementForm", p) : NoSetupRedirect(personId);
    }

    [HttpPost("increment")]
    public async Task<IActionResult> Increment(int personId, IncrementFormViewModel form, CancellationToken cancellationToken)
    {
        var page = await IncrementPageAsync(personId, null, form, cancellationToken);
        if (page is null)
        {
            return NotFound();
        }

        if (page.Value.Page is not { } p)
        {
            return NoSetupRedirect(personId);
        }

        if (!ModelState.IsValid)
        {
            return View("IncrementForm", p);
        }

        var result = await pay.CreateIncrementAsync(personId, form.EffectiveFrom(), form.Pay, form.Note, ActorId, cancellationToken);
        if (result.Succeeded)
        {
            TempData.ToastSuccess("Pay change recorded.");
            return RedirectToDetails(personId);
        }

        if (result.Status == PayResultStatus.NoSetup)
        {
            return NoSetupRedirect(personId);
        }

        AddErrors(result);
        return View("IncrementForm", p);
    }

    [HttpGet("{recordId:int}/increment-edit")]
    public async Task<IActionResult> EditIncrement(int personId, int recordId, CancellationToken cancellationToken)
    {
        var record = await pay.GetOwnRecordAsync(personId, recordId, ActorId, cancellationToken);
        if (record is null)
        {
            return NotFound(); // missing, or created by someone else
        }

        var form = new IncrementFormViewModel { Pay = record.PayMonthlyAmount, Note = record.Note, RowVersion = Convert.ToBase64String(record.RowVersion) };
        form.SetEffectiveFrom(record.EffectiveFrom);
        var page = await IncrementPageAsync(personId, recordId, form, cancellationToken);
        return page?.Page is { } p ? View("IncrementForm", p with { Currency = record.PayCurrency }) : NotFound();
    }

    [HttpPost("{recordId:int}/increment-edit")]
    public async Task<IActionResult> EditIncrement(int personId, int recordId, IncrementFormViewModel form, CancellationToken cancellationToken)
    {
        var record = await pay.GetOwnRecordAsync(personId, recordId, ActorId, cancellationToken);
        if (record is null)
        {
            return NotFound();
        }

        var page = await IncrementPageAsync(personId, recordId, form, cancellationToken);
        if (page?.Page is not { } p)
        {
            return NotFound();
        }

        p = p with { Currency = record.PayCurrency };
        if (!ModelState.IsValid)
        {
            return View("IncrementForm", p);
        }

        if (!TryRowVersion(form.RowVersion, out var rowVersion))
        {
            return BadRequest();
        }

        var result = await pay.UpdateIncrementAsync(personId, recordId, form.EffectiveFrom(), form.Pay, form.Note, rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayResultStatus.Success:
                TempData.ToastSuccess("Pay change updated.");
                return RedirectToDetails(personId);
            case PayResultStatus.NotFound:
                return NotFound();
            case PayResultStatus.Forbidden:
                return Forbid();
            case PayResultStatus.Locked:
                return LockedRedirect(personId, result);
            default:
                AddErrors(result);
                return View("IncrementForm", p);
        }
    }

    // ===================== Helpers =====================

    private async Task<(PersonDetails? Person, AdminPayTab? Tab)> LoadAdminAsync(int personId, CancellationToken cancellationToken) =>
        (await people.GetAsync(personId, cancellationToken), await pay.GetAdminTabAsync(personId, cancellationToken));

    /// <summary>Null: the person doesn't exist. Page null: no pay setup yet (neutral message).</summary>
    private async Task<(IncrementFormPage? Page, bool Found)?> IncrementPageAsync(int personId, int? recordId, IncrementFormViewModel? form, CancellationToken cancellationToken)
    {
        var person = await people.GetAsync(personId, cancellationToken);
        var tab = await pay.GetManagerTabAsync(personId, ActorId, cancellationToken);
        if (person is null || tab is null)
        {
            return null;
        }

        var currency = await pay.GetIncrementCurrencyAsync(personId, cancellationToken);
        if (!tab.HasSetup || currency is null)
        {
            return (null, true);
        }

        if (form is null)
        {
            form = new IncrementFormViewModel();
            form.SetEffectiveFrom(tab.DefaultEffectiveFrom);
        }

        return (new IncrementFormPage(personId, person.FullName, person.Code, recordId, currency.Value, tab.Current, form, tab.EarliestEffectiveFrom), true);
    }

    private AdminPayFormPage Failed(AdminPayFormPage page, PayResult result)
    {
        switch (result.Status)
        {
            case PayResultStatus.NeedsLossConfirmation:
                page.Form.ConfirmLoss = false;
                ModelState.Remove(nameof(AdminPayFormViewModel.ConfirmLoss));
                ModelState.AddModelError(nameof(AdminPayFormViewModel.ConfirmLoss), LossConfirmMessage);
                return page with { LossPayUsd = result.LossPayUsd };
            case PayResultStatus.Conflict:
                ModelState.AddModelError(string.Empty, ConflictMessage);
                return page;
            default:
                AddErrors(result);
                return page;
        }
    }

    private void AddErrors(PayResult result)
    {
        if (result.Status == PayResultStatus.Conflict)
        {
            ModelState.AddModelError(string.Empty, ConflictMessage);
            return;
        }

        foreach (var error in result.Errors ?? [new PayError(string.Empty, "The pay record could not be saved.")])
        {
            // Effective-from errors belong to the month picker.
            ModelState.AddModelError(error.Field == PayFields.EffectiveFrom ? nameof(PayPeriodPickerForm.EffectiveMonth) : error.Field, error.Message);
        }
    }

    private IActionResult NoSetupRedirect(int personId)
    {
        TempData.ToastError(PayRules.NoHireSourceMessage);
        return RedirectToDetails(personId);
    }

    private IActionResult LockedRedirect(int personId, PayResult result)
    {
        TempData.ToastError(result.Errors?.FirstOrDefault()?.Message ?? PayRecordService.LockedMessage);
        return RedirectToDetails(personId);
    }

    private RedirectToActionResult RedirectToDetails(int personId) =>
        RedirectToAction(nameof(PeopleController.Details), "People", new { id = personId }, "pay");

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
