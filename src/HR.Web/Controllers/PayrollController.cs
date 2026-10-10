using System.Security.Claims;
using HR.Domain.Payroll;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Invoices;
using HR.Infrastructure.Payroll;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Settings;
using HR.Web.Formatting;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace HR.Web.Controllers;

/// <summary>
/// The payroll engine (SPEC §5–§6) for Managers and Admins. Billing values (billed, invoice, the Company's earning, hire
/// source) are loaded only for Admins; the payslip shows none of them to anyone.
/// </summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("payroll")]
public class PayrollController(PayrollService payroll, IExchangeRateService rates, SettingsService settings, InvoiceService invoices, IMemoryCache cache) : Controller
{
    private const string ChangesKey = "PayrollChanges";

    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    private bool IsAdmin => User.IsInRole(AppRoles.Admin);


    // ===================== List and generate =====================

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var runs = await payroll.ListAsync(cancellationToken);
        var totals = IsAdmin ? await payroll.AdminTotalsAsync(cancellationToken) : null;
        return View(new PayrollIndexViewModel(runs, totals, GeneratePayrollForm.For(await payroll.DefaultPeriodAsync(cancellationToken))));
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate(GeneratePayrollForm form, CancellationToken cancellationToken)
    {
        if (form.PeriodStart() is not { } start)
        {
            TempData.ToastError("Choose the month and half of the payroll period.");
            return RedirectToAction(nameof(Index));
        }

        var result = await payroll.GenerateAsync(start, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.Success:
                TempData.ToastSuccess($"Draft payroll for {DisplayFormat.DateRange(PayPeriod.For(start).Start, PayPeriod.For(start).End)} generated.");
                return RedirectToAction(nameof(Run), new { id = result.Id });
            case PayrollResultStatus.Exists when result.Id is { } existing:
                TempData.ToastError(PayrollService.ExistsMessage);
                return RedirectToAction(nameof(Run), new { id = existing });
            default:
                TempData.ToastError(result.Message ?? "The payroll could not be generated.");
                return RedirectToAction(nameof(Index));
        }
    }

    // ===================== Run page and workflow =====================

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Run(int id, CancellationToken cancellationToken)
    {
        var run = await payroll.GetRunAsync(id, cancellationToken);
        if (run is null)
        {
            return NotFound();
        }

        var lines = await payroll.LinesAsync(id, cancellationToken);
        var billing = IsAdmin ? await payroll.BillingAsync(id, cancellationToken: cancellationToken) : null;
        var history = run.IsDraft ? await rates.GetRateForAsync(run.Period.End, cancellationToken) : null;
        return View(new PayrollRunViewModel(run, lines, billing, await payroll.EventsAsync(id, cancellationToken), ReadChanges(), history?.UsdToPkr,
            history?.EffectiveFrom, new RateForm { Source = run.RateOverridden ? "override" : "history", Rate = run.ExchangeRate },
            IsAdmin && !run.IsDraft ? await invoices.RunInvoiceAsync(id, cancellationToken) : null));
    }

    [HttpPost("{id:int}/regenerate")]
    public async Task<IActionResult> Regenerate(int id, CancellationToken cancellationToken)
    {
        var result = await payroll.RegenerateAsync(id, ActorId, cancellationToken);
        if (result.Status == PayrollResultStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            var changes = Visible(result.Changes);
            KeepChanges(changes);
            TempData.ToastSuccess(changes.Count == 0 ? "Recalculated: nothing changed." : $"Recalculated: {changes.Count} {(changes.Count == 1 ? "value" : "values")} changed.");
        }
        else
        {
            TempData.ToastError(result.Message ?? PayrollService.NotDraftMessage);
        }

        return RedirectToAction(nameof(Run), new { id });
    }

    [HttpPost("{id:int}/rate")]
    public async Task<IActionResult> Rate(int id, RateForm form, CancellationToken cancellationToken)
    {
        var result = await payroll.SetRateAsync(id, form.Source == "history", form.Rate, form.RateNote, ActorId, cancellationToken);
        if (result.Status == PayrollResultStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            TempData.ToastSuccess("Exchange rate updated; PKR amounts recalculated.");
        }
        else
        {
            TempData.ToastError(result.Message ?? "The rate could not be changed.");
        }

        return RedirectToAction(nameof(Run), null, new { id }, "rate");
    }

    [HttpPost("{id:int}/finalize")]
    public async Task<IActionResult> Finalize(int id, CancellationToken cancellationToken)
    {
        var result = await payroll.FinalizeAsync(id, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.NotFound:
                return NotFound();
            case PayrollResultStatus.Success:
                TempData.ToastSuccess("Payroll finalized. Its figures are now locked.");
                break;
            case PayrollResultStatus.DataChanged:
                KeepChanges(Visible(result.Changes));
                TempData.ToastError(PayrollService.DataChangedMessage);
                break;
            default:
                TempData.ToastError(result.Message ?? "The payroll could not be finalized.");
                break;
        }

        return RedirectToAction(nameof(Run), new { id });
    }

    [HttpPost("{id:int}/reopen")]
    [Authorize(Policy = Policies.AdminOnly)]
    public async Task<IActionResult> Reopen(int id, ReopenForm form, CancellationToken cancellationToken)
    {
        var result = await payroll.ReopenAsync(id, form.Reason, ActorId, cancellationToken);
        if (result.Status == PayrollResultStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Succeeded)
        {
            TempData.ToastSuccess("Payroll reopened as a draft.");
        }
        else
        {
            TempData.ToastError(result.Message ?? "The payroll could not be reopened.");
        }

        return RedirectToAction(nameof(Run), new { id });
    }

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await payroll.DeleteAsync(id, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.NotFound:
                return NotFound();
            case PayrollResultStatus.Success:
                TempData.ToastSuccess("Draft payroll deleted.");
                return RedirectToAction(nameof(Index));
            default:
                TempData.ToastError(result.Message ?? "The payroll could not be deleted.");
                return RedirectToAction(nameof(Run), new { id });
        }
    }

    // ===================== Line page, extra days, adjustments =====================

    [HttpGet("{id:int}/lines/{lineId:int}")]
    public async Task<IActionResult> Line(int id, int lineId, CancellationToken cancellationToken)
    {
        var page = await LinePageAsync(id, lineId, null, null, cancellationToken);
        return page is null ? NotFound() : View(page);
    }

    [HttpPost("{id:int}/lines/{lineId:int}/extra-days")]
    public async Task<IActionResult> ExtraDays(int id, int lineId, ExtraDaysForm form, bool clear, CancellationToken cancellationToken)
    {
        var result = await payroll.SetExtraDaysAsync(id, lineId, clear ? 0m : form.ExtraDays, clear ? null : form.ExtraDaysNote, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.NotFound:
                return NotFound();
            case PayrollResultStatus.Success:
                TempData.ToastSuccess(clear ? "Extra days cleared." : "Extra days saved.");
                return RedirectToLine(id, lineId);
            case PayrollResultStatus.Invalid:
                AddErrors(result, "ExtraDays.");
                var page = await LinePageAsync(id, lineId, form, null, cancellationToken);
                return page is null ? NotFound() : View(nameof(Line), page);
            default:
                TempData.ToastError(result.Message ?? PayrollService.NotDraftMessage);
                return RedirectToLine(id, lineId);
        }
    }

    [HttpPost("{id:int}/lines/{lineId:int}/adjustments")]
    public async Task<IActionResult> AddAdjustment(int id, int lineId, AdjustmentForm form, CancellationToken cancellationToken)
    {
        var result = await payroll.AddAdjustmentAsync(id, lineId, form.ParsedType(), form.Amount, form.ParsedCurrency(), form.Note, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.NotFound:
                return NotFound();
            case PayrollResultStatus.Success:
                TempData.ToastSuccess("Adjustment added.");
                return RedirectToLine(id, lineId, "adjustments");
            case PayrollResultStatus.Invalid:
                AddErrors(result, "Adjustment.");
                var page = await LinePageAsync(id, lineId, null, form, cancellationToken);
                return page is null ? NotFound() : View(nameof(Line), page);
            default:
                TempData.ToastError(result.Message ?? PayrollService.NotDraftMessage);
                return RedirectToLine(id, lineId);
        }
    }

    [HttpGet("{id:int}/lines/{lineId:int}/adjustments/{adjustmentId:int}/edit")]
    public async Task<IActionResult> EditAdjustment(int id, int lineId, int adjustmentId, CancellationToken cancellationToken)
    {
        var run = await payroll.GetRunAsync(id, cancellationToken);
        var detail = await payroll.GetLineAsync(id, lineId, cancellationToken);
        var adjustment = detail?.Adjustments.SingleOrDefault(a => a.Id == adjustmentId);
        if (run is null || detail is null || adjustment is null)
        {
            return NotFound();
        }

        if (!run.IsDraft)
        {
            TempData.ToastError(PayrollService.NotDraftMessage);
            return RedirectToLine(id, lineId);
        }

        var form = new AdjustmentForm
        {
            Type = adjustment.Type.ToString(),
            Amount = adjustment.Amount,
            Currency = adjustment.Currency.ToString(),
            Note = adjustment.Note,
            RowVersion = Convert.ToBase64String(adjustment.RowVersion),
        };
        return View(new AdjustmentEditViewModel(run, detail.Line, adjustmentId, form));
    }

    [HttpPost("{id:int}/lines/{lineId:int}/adjustments/{adjustmentId:int}/edit")]
    public async Task<IActionResult> EditAdjustment(int id, int lineId, int adjustmentId, AdjustmentForm form, CancellationToken cancellationToken)
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

        var result = await payroll.UpdateAdjustmentAsync(id, lineId, adjustmentId, form.ParsedType(), form.Amount, form.ParsedCurrency(), form.Note, rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.NotFound:
                return NotFound();
            case PayrollResultStatus.Success:
                TempData.ToastSuccess("Adjustment updated.");
                return RedirectToLine(id, lineId, "adjustments");
            case PayrollResultStatus.Invalid or PayrollResultStatus.Conflict:
                AddErrors(result, string.Empty);
                var run = await payroll.GetRunAsync(id, cancellationToken);
                var detail = await payroll.GetLineAsync(id, lineId, cancellationToken);
                return run is null || detail is null ? NotFound() : View(new AdjustmentEditViewModel(run, detail.Line, adjustmentId, form));
            default:
                TempData.ToastError(result.Message ?? PayrollService.NotDraftMessage);
                return RedirectToLine(id, lineId);
        }
    }

    [HttpPost("{id:int}/lines/{lineId:int}/adjustments/{adjustmentId:int}/delete")]
    public async Task<IActionResult> DeleteAdjustment(int id, int lineId, int adjustmentId, CancellationToken cancellationToken)
    {
        var result = await payroll.DeleteAdjustmentAsync(id, lineId, adjustmentId, ActorId, cancellationToken);
        switch (result.Status)
        {
            case PayrollResultStatus.NotFound:
                return NotFound();
            case PayrollResultStatus.Success:
                TempData.ToastSuccess("Adjustment deleted.");
                break;
            default:
                TempData.ToastError(result.Message ?? PayrollService.NotDraftMessage);
                break;
        }

        // The line disappears when its last orphaned entry goes; fall back to the run.
        return await payroll.GetLineAsync(id, lineId, cancellationToken) is null ? RedirectToAction(nameof(Run), new { id }) : RedirectToLine(id, lineId, "adjustments");
    }

    // ===================== Payslips and register =====================

    [HttpGet("{id:int}/lines/{lineId:int}/payslip")]
    public async Task<IActionResult> Payslip(int id, int lineId, CancellationToken cancellationToken)
    {
        var run = await payroll.GetRunAsync(id, cancellationToken);
        var detail = await payroll.GetLineAsync(id, lineId, cancellationToken);
        return run is null || detail is null || detail.Line.IsOrphaned ? NotFound() : View(new PayslipViewModel(run, detail, (await settings.GetAsync(cancellationToken)).PayslipIssuerName));
    }

    [HttpGet("{id:int}/payslips")]
    public async Task<IActionResult> Payslips(int id, CancellationToken cancellationToken)
    {
        var run = await payroll.GetRunAsync(id, cancellationToken);
        return run is null ? NotFound() : View(new PayslipsViewModel(run, await payroll.GetAllLineDetailsAsync(id, cancellationToken), (await settings.GetAsync(cancellationToken)).PayslipIssuerName));
    }

    [HttpGet("{id:int}/register")]
    public async Task<IActionResult> Register(int id, CancellationToken cancellationToken)
    {
        var run = await payroll.GetRunAsync(id, cancellationToken);
        return run is null ? NotFound() : View(new RegisterViewModel(run, await payroll.RegisterAsync(id, cancellationToken), (await settings.GetAsync(cancellationToken)).PayslipIssuerName));
    }

    // ===================== Helpers =====================

    private async Task<PayrollLineViewModel?> LinePageAsync(int id, int lineId, ExtraDaysForm? extra, AdjustmentForm? adjustment, CancellationToken cancellationToken)
    {
        var run = await payroll.GetRunAsync(id, cancellationToken);
        var detail = await payroll.GetLineAsync(id, lineId, cancellationToken);
        if (run is null || detail is null)
        {
            return null;
        }

        var billing = IsAdmin ? (await payroll.BillingAsync(id, lineId, cancellationToken)).GetValueOrDefault(lineId) : null;
        extra ??= new ExtraDaysForm { ExtraDays = detail.Line.ExtraDays == 0m ? null : detail.Line.ExtraDays, ExtraDaysNote = detail.ExtraDaysNote };
        return new PayrollLineViewModel(run, detail, billing, extra, adjustment ?? new AdjustmentForm());
    }

    /// <summary>Managers never see billing differences.</summary>
    private List<LineChange> Visible(IReadOnlyList<LineChange>? changes) =>
        (changes ?? []).Where(c => IsAdmin || !c.AdminOnly).ToList();

    /// <summary>
    /// Keeps the recalculation diff for the next page view. The list can be long, so it stays in server memory for ten
    /// minutes under a random key; only the key travels in the TempData cookie, scoped to this user.
    /// </summary>
    private void KeepChanges(List<LineChange> changes)
    {
        if (changes.Count > 0)
        {
            var key = Guid.NewGuid().ToString("N");
            cache.Set(CacheKey(key), changes, TimeSpan.FromMinutes(10));
            TempData[ChangesKey] = key;
        }
    }

    private IReadOnlyList<LineChange>? ReadChanges()
    {
        if (TempData[ChangesKey] is not string key || !cache.TryGetValue(CacheKey(key), out List<LineChange>? changes) || changes is null)
        {
            return null;
        }

        return changes.Where(c => IsAdmin || !c.AdminOnly).ToList();
    }

    private string CacheKey(string key) => $"payroll-changes:{ActorId}:{key}";

    private void AddErrors(PayrollResult result, string prefix)
    {
        foreach (var error in result.Errors ?? [])
        {
            ModelState.AddModelError(string.IsNullOrEmpty(error.Field) ? string.Empty : prefix + error.Field, error.Message);
        }
    }

    private RedirectToActionResult RedirectToLine(int id, int lineId, string? fragment = null) =>
        RedirectToAction(nameof(Line), null, new { id, lineId }, fragment);
}
