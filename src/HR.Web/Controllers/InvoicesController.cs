using System.Security.Claims;
using HR.Infrastructure.Invoices;
using HR.Infrastructure.Settings;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>Company invoices (SPEC §7, M8). Admin only: Managers get 403 on every route.</summary>
[Authorize(Policy = Policies.AdminOnly)]
[Route("invoices")]
public class InvoicesController(InvoiceService invoices, SettingsService settings) : Controller
{
    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    [HttpGet("")]
    public async Task<IActionResult> Index(string? status, int? year, CancellationToken cancellationToken)
    {
        var filter = Enum.GetNames<InvoiceStatusFilter>().FirstOrDefault(n => string.Equals(n, status, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<InvoiceStatusFilter>(name)
            : InvoiceStatusFilter.All;
        var list = await invoices.ListAsync(filter, year, cancellationToken);
        var canIssue = (await settings.GetAsync(cancellationToken)).CanIssueInvoices;
        return View(new InvoiceListViewModel(list, filter, year, canIssue));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var details = await invoices.GetAsync(id, cancellationToken);
        return details is null ? NotFound() : View(new InvoiceViewModel(details, new MarkPaidForm { PaidDate = null, AmountReceivedUsd = details.Invoice.TotalUsd }));
    }

    [HttpPost("{id:int}/paid")]
    public async Task<IActionResult> MarkPaid(int id, MarkPaidForm form, CancellationToken cancellationToken)
    {
        if (!TryRowVersion(form.RowVersion, out var rowVersion))
        {
            return BadRequest();
        }

        var result = await invoices.MarkPaidAsync(id, form.PaidDate, form.AmountReceivedUsd, form.PaymentNote, rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case InvoiceResultStatus.NotFound:
                return NotFound();
            case InvoiceResultStatus.Success:
                TempData.ToastSuccess("Invoice marked paid.");
                return RedirectToAction(nameof(Details), new { id });
            case InvoiceResultStatus.Invalid:
                ModelState.AddModelError(result.Field ?? string.Empty, result.Message ?? "Check the payment details.");
                var details = await invoices.GetAsync(id, cancellationToken);
                return details is null ? NotFound() : View(nameof(Details), new InvoiceViewModel(details, form, ShowPayment: true));
            default:
                TempData.ToastError(result.Message ?? "The invoice could not be updated.");
                return RedirectToAction(nameof(Details), new { id });
        }
    }

    [HttpPost("{id:int}/unpaid")]
    public async Task<IActionResult> MarkUnpaid(int id, string? rowVersion, CancellationToken cancellationToken)
    {
        if (!TryRowVersion(rowVersion, out var version))
        {
            return BadRequest();
        }

        var result = await invoices.MarkUnpaidAsync(id, version, ActorId, cancellationToken);
        if (result.Status == InvoiceResultStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            TempData.ToastSuccess("Invoice marked unpaid.");
        }
        else
        {
            TempData.ToastError(result.Message ?? "The invoice could not be updated.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Issues the pending invoice of a finalized payroll once Settings are complete.</summary>
    [HttpPost("issue/{runId:int}")]
    public async Task<IActionResult> Issue(int runId, CancellationToken cancellationToken)
    {
        var result = await invoices.IssuePendingAsync(runId, ActorId, cancellationToken);
        switch (result.Status)
        {
            case InvoiceResultStatus.NotFound:
                return NotFound();
            case InvoiceResultStatus.Success:
                TempData.ToastSuccess("Invoice issued.");
                return RedirectToAction(nameof(Details), new { id = result.Id });
            default:
                TempData.ToastError(result.Message ?? "The invoice could not be issued.");
                return RedirectToAction(nameof(PayrollController.Run), "Payroll", new { id = runId });
        }
    }

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
