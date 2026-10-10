using System.Security.Claims;
using HR.Infrastructure.Settings;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>Business, client, invoice and payslip settings (M8 Part B). Admin only.</summary>
[Authorize(Policy = Policies.AdminOnly)]
[Route("admin/settings")]
public class SettingsController(SettingsService settings) : Controller
{
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken);
        return View(new SettingsPage(SettingsForm.From(current), current.CanIssueInvoices, current.UpdatedAt, Conflict: false));
    }

    [HttpPost("")]
    public async Task<IActionResult> Index(SettingsForm form, CancellationToken cancellationToken)
    {
        byte[] rowVersion;
        try
        {
            rowVersion = Convert.FromBase64String(form.RowVersion ?? string.Empty);
        }
        catch (FormatException)
        {
            return BadRequest();
        }

        var result = await settings.UpdateAsync(form.ToInput(), rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case SettingsResultStatus.Success:
                TempData.ToastSuccess(result.ChangedFields is { Count: > 0 } changed ? $"Settings saved ({changed.Count} {(changed.Count == 1 ? "field" : "fields")} changed)." : "No changes to save.");
                return RedirectToAction(nameof(Index));
            case SettingsResultStatus.Conflict:
                var current = await settings.GetAsync(cancellationToken);
                ModelState.Clear();
                ModelState.AddModelError(string.Empty, SettingsService.ConflictMessage);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return View(new SettingsPage(SettingsForm.From(current), current.CanIssueInvoices, current.UpdatedAt, Conflict: true));
            default:
                foreach (var error in result.Errors ?? [])
                {
                    ModelState.AddModelError(error.Field, error.Message);
                }

                var saved = await settings.GetAsync(cancellationToken);
                return View(new SettingsPage(form, saved.CanIssueInvoices, saved.UpdatedAt, Conflict: false));
        }
    }
}
