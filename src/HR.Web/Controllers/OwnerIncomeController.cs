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
    public async Task<IActionResult> Index(string? view, string? at, bool draft, CancellationToken cancellationToken)
    {
        var selected = Enum.GetNames<IncomeView>().FirstOrDefault(n => string.Equals(n, view, StringComparison.OrdinalIgnoreCase)) is { } name
            ? Enum.Parse<IncomeView>(name)
            : IncomeView.Month;
        DateOnly? anchor = DateOnly.TryParseExact(at, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Year is >= 2000 and <= 2100
            ? parsed
            : null;

        var report = await income.GetAsync(selected, anchor, draft, cancellationToken);
        return View(new OwnerIncomeViewModel(report, draft));
    }
}
