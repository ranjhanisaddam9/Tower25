using System.Security.Claims;
using HR.Infrastructure.Identity;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>Admin-only management of Manager accounts (SPEC §1).</summary>
[Authorize(Policy = Policies.AdminOnly)]
[Route("admin/managers")]
public class ManagersController(ManagerService managers) : Controller
{
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var filter = Enum.TryParse<ManagerStatusFilter>(status, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : ManagerStatusFilter.All;
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];

        var result = await managers.ListAsync(new ManagerListQuery(search, filter, page), cancellationToken);
        var rows = result.Items
            .Select(m => new ManagerRowViewModel(m.Id, m.FullName, m.Email, m.IsActive, m.MustChangePassword, m.LastLoginAt))
            .ToList();

        return View(new ManagerListViewModel(rows, search, filter, result.Page, result.TotalPages, result.TotalCount));
    }

    [HttpGet("create")]
    public IActionResult Create() => View(new ManagerFormViewModel());

    [HttpPost("create")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Create(ManagerFormViewModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(form);
        }

        var result = await managers.CreateAsync(form.FullName, form.Email, ActorId, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(form);
        }

        // Rendered straight from this POST: the password is never stored, redirected through TempData or logged.
        return View("TemporaryPassword", new TemporaryPasswordViewModel(form.FullName.Trim(), form.Email.Trim(), result.TemporaryPassword!, IsReset: false));
    }

    [HttpGet("{id}/edit")]
    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var manager = await managers.FindAsync(id, cancellationToken);
        if (manager is null)
        {
            return NotFound();
        }

        return View(new ManagerEditViewModel(manager.Id, manager.IsActive, new ManagerFormViewModel { FullName = manager.FullName, Email = manager.Email }));
    }

    [HttpPost("{id}/edit")]
    public async Task<IActionResult> Edit(string id, ManagerFormViewModel form, CancellationToken cancellationToken)
    {
        var manager = await managers.FindAsync(id, cancellationToken);
        if (manager is null)
        {
            return NotFound();
        }

        if (ModelState.IsValid)
        {
            var result = await managers.UpdateAsync(id, form.FullName, form.Email, ActorId, cancellationToken);
            if (result.Succeeded)
            {
                TempData.ToastSuccess($"{form.FullName.Trim()} has been updated.");
                return RedirectToAction(nameof(Index));
            }

            AddErrors(result);
        }

        return View(new ManagerEditViewModel(manager.Id, manager.IsActive, form));
    }

    [HttpPost("{id}/deactivate")]
    public Task<IActionResult> Deactivate(string id, CancellationToken cancellationToken) => SetActive(id, active: false, cancellationToken);

    [HttpPost("{id}/activate")]
    public Task<IActionResult> Activate(string id, CancellationToken cancellationToken) => SetActive(id, active: true, cancellationToken);

    [HttpPost("{id}/reset-password")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> ResetPassword(string id, CancellationToken cancellationToken)
    {
        var manager = await managers.FindAsync(id, cancellationToken);
        if (manager is null)
        {
            return NotFound();
        }

        var result = await managers.ResetPasswordAsync(id, ActorId, cancellationToken);
        if (!result.Succeeded)
        {
            TempData.ToastError("The password could not be reset. Please try again.");
            return RedirectToAction(nameof(Edit), new { id });
        }

        return View("TemporaryPassword", new TemporaryPasswordViewModel(manager.FullName, manager.Email, result.TemporaryPassword!, IsReset: true));
    }

    private async Task<IActionResult> SetActive(string id, bool active, CancellationToken cancellationToken)
    {
        var result = await managers.SetActiveAsync(id, active, ActorId, cancellationToken);
        switch (result.Status)
        {
            case ManagerResultStatus.Success:
                var manager = await managers.FindAsync(id, cancellationToken);
                TempData.ToastSuccess(active
                    ? $"{manager?.FullName} has been activated."
                    : $"{manager?.FullName} has been deactivated and signed out.");
                break;
            case ManagerResultStatus.CannotChangeSelf:
                TempData.ToastError("You can't deactivate your own account.");
                break;
            case ManagerResultStatus.NotFound:
                return NotFound();
            default:
                TempData.ToastError("The change could not be saved. Please try again.");
                break;
        }

        return RedirectToAction(nameof(Index));
    }

    private void AddErrors(ManagerResult result)
    {
        if (result.Status == ManagerResultStatus.DuplicateEmail)
        {
            ModelState.AddModelError(nameof(ManagerFormViewModel.Email), "Another user already has this email.");
            return;
        }

        foreach (var error in result.Errors ?? ["The change could not be saved."])
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
