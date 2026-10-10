using System.Security.Claims;
using HR.Domain.Rates;
using HR.Domain.Time;
using HR.Infrastructure.Rates;
using HR.Web.Formatting;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>USD→PKR rate history (SPEC §2). Managers and Admins have the same access.</summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("exchange-rates")]
public class ExchangeRatesController(ExchangeRateService rates, IClock clock) : Controller
{
    public const string ConflictMessage =
        "Someone else saved changes to this rate while you were editing. The form now shows the latest saved values; review the differences below and save again.";

    public const string ConfirmLargeChangeMessage = "Tick \"I've double-checked this rate\" to save a change of more than 5%.";

    private string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Signed-in user has no id claim.");

    /// <summary>The whole rate history as .xlsx (M9), newest first.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromServices] HR.Web.Exports.Downloads downloads, CancellationToken cancellationToken)
    {
        var list = await rates.ListAsync(1, HR.Infrastructure.Exports.ExportLimits.MaxRows, cancellationToken);
        IReadOnlyList<HR.Infrastructure.Exports.ExportFilter> filters = [new("Entries", "All")];
        return downloads.Send(this, HR.Web.Exports.ExcelExports.ExchangeRates(downloads.Context(User, "Exchange rates", filters), list.Items, clock.Today),
            "Exchange rates", filters);
    }

    /// <param name="on">Optional "rate on date" lookup.</param>
    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, DateOnly? on = null, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var overview = await rates.GetOverviewAsync(cancellationToken);
        var list = await rates.ListAsync(page, cancellationToken: cancellationToken);
        var chart = RateChartViewModel.Build(await rates.GetChartEntriesAsync(cancellationToken), today);
        RateLookupViewModel? lookup = on is { } date ? new RateLookupViewModel(date, await rates.GetRateForAsync(date, cancellationToken)) : null;

        var rows = list.Items
            .Select(r => new RateRowViewModel(r.Id, r.EffectiveFrom, r.UsdToPkr, RateChangeViewModel.Between(r.PreviousUsdToPkr, r.UsdToPkr),
                r.Note, r.AddedBy, r.EffectiveFrom > today))
            .ToList();

        return View(new ExchangeRatesPageViewModel(
            overview.Current,
            overview.Current is { } current ? RateChangeViewModel.Between(overview.Previous?.UsdToPkr, current.UsdToPkr) : null,
            overview.Previous,
            rows,
            list.Page,
            list.TotalPages,
            list.TotalCount,
            chart,
            on ?? today,
            lookup));
    }

    [HttpGet("create")]
    public IActionResult Create() => View("Edit", new ExchangeRateEditViewModel(null, new ExchangeRateFormViewModel { EffectiveFrom = clock.Today }));

    [HttpPost("create")]
    public async Task<IActionResult> Create(ExchangeRateFormViewModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", new ExchangeRateEditViewModel(null, form));
        }

        var result = await rates.CreateAsync(form.ToInput(), form.ConfirmLargeChange, ActorId, cancellationToken);
        if (result.Succeeded)
        {
            TempData.ToastSuccess($"Rate of Rs {ExchangeRateRules.Format(form.UsdToPkr!.Value)} from {DisplayFormat.Date(form.EffectiveFrom!.Value)} added.");
            return RedirectToAction(nameof(Index));
        }

        return View("Edit", Failed(null, form, result));
    }

    [HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
    {
        var rate = await rates.GetAsync(id, cancellationToken);
        return rate is null ? NotFound() : View(new ExchangeRateEditViewModel(id, ExchangeRateFormViewModel.From(rate)));
    }

    [HttpPost("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id, ExchangeRateFormViewModel form, CancellationToken cancellationToken)
    {
        if (await rates.GetAsync(id, cancellationToken) is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(new ExchangeRateEditViewModel(id, form));
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

        var result = await rates.UpdateAsync(id, form.ToInput(), form.ConfirmLargeChange, rowVersion, ActorId, cancellationToken);
        switch (result.Status)
        {
            case RateResultStatus.Success:
                TempData.ToastSuccess($"Rate from {DisplayFormat.Date(form.EffectiveFrom!.Value)} updated.");
                return RedirectToAction(nameof(Index));
            case RateResultStatus.NotFound:
                return NotFound();
            case RateResultStatus.Conflict:
                var current = result.Current!;
                ModelState.Clear();
                ModelState.AddModelError(string.Empty, ConflictMessage);
                Response.StatusCode = StatusCodes.Status409Conflict;
                return View(new ExchangeRateEditViewModel(id, ExchangeRateFormViewModel.From(current), ConflictChanges: Differences(form, current)));
            default:
                return View(Failed(id, form, result));
        }
    }

    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var existing = await rates.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        await rates.DeleteAsync(id, ActorId, cancellationToken);
        TempData.ToastSuccess($"Rate from {DisplayFormat.Date(existing.EffectiveFrom)} deleted.");
        return RedirectToAction(nameof(Index));
    }

    private ExchangeRateEditViewModel Failed(int? id, ExchangeRateFormViewModel form, RateResult result)
    {
        if (result.Status == RateResultStatus.NeedsConfirmation)
        {
            // Server-side rule: without the box ticked, a >5% change is never saved.
            form.ConfirmLargeChange = false;
            ModelState.AddModelError(nameof(ExchangeRateFormViewModel.ConfirmLargeChange), ConfirmLargeChangeMessage);
            return new ExchangeRateEditViewModel(id, form, result.LargeChange);
        }

        foreach (var error in result.Errors ?? [new RateError(string.Empty, "The rate could not be saved.")])
        {
            ModelState.AddModelError(error.Field, error.Message);
        }

        return new ExchangeRateEditViewModel(id, form);
    }

    private static List<FieldChangeViewModel> Differences(ExchangeRateFormViewModel yours, RateDetails saved)
    {
        var changes = new List<FieldChangeViewModel>();
        void Compare(string label, string savedValue, string yourValue)
        {
            if (!string.Equals(savedValue, yourValue, StringComparison.Ordinal))
            {
                changes.Add(new FieldChangeViewModel(label, savedValue, yourValue));
            }
        }

        Compare("Effective from", DisplayFormat.Date(saved.EffectiveFrom), yours.EffectiveFrom is { } d ? DisplayFormat.Date(d) : "(empty)");
        Compare("Rate", ExchangeRateRules.Format(saved.UsdToPkr), yours.UsdToPkr is { } r ? ExchangeRateRules.Format(r) : "(empty)");
        Compare("Note", saved.Note ?? "(empty)", string.IsNullOrWhiteSpace(yours.Note) ? "(empty)" : yours.Note.Trim());
        return changes;
    }
}
