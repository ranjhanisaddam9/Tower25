using System.Globalization;
using HR.Domain.Payroll;
using HR.Infrastructure.Payroll;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>Owner income (SPEC §8, M8). Admin only; read-only, from finalized payroll lines (drafts only on request).</summary>
[Authorize(Policy = Policies.AdminOnly)]
[Route("owner-income")]
public class OwnerIncomeController(OwnerIncomeService income) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? view, string? at, bool draft, CancellationToken cancellationToken) =>
        View(await LoadAsync(view, at, draft, cancellationToken));

    /// <summary>The selected view's breakdown and its contributors as .xlsx (M9), from frozen line values.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export(string? view, string? at, bool draft, [FromServices] HR.Web.Exports.Downloads downloads, CancellationToken cancellationToken)
    {
        var model = await LoadAsync(view, at, draft, cancellationToken);
        IReadOnlyList<HR.Infrastructure.Exports.ExportFilter> filters =
        [
            new("View", model.View.ToString()),
            new("Selection", model.SelectionLabel),
            new("Draft payroll", draft ? "Included separately (projected)" : "Not included"),
        ];
        return downloads.Send(this, HR.Web.Exports.ExcelExports.OwnerIncome(downloads.Context(User, "Owner income", filters), model), "Owner income", filters);
    }

    private async Task<OwnerIncomeViewModel> LoadAsync(string? view, string? at, bool draft, CancellationToken cancellationToken)
    {
        var selected = Enum.GetNames<IncomeView>().FirstOrDefault(n => string.Equals(n, view, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<IncomeView>(name)
            : IncomeView.Month;
        DateOnly? anchor = DateOnly.TryParseExact(at, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Year is >= 2000 and <= 2100
            ? parsed
            : null;

        return new OwnerIncomeViewModel(await income.GetAsync(selected, anchor, draft, cancellationToken), draft);
    }
}
