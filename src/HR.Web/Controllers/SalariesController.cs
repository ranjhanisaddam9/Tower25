using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>Current pay for every active person. Admins also see billing, commission, estimated earning and reviews.</summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("salaries")]
public class SalariesController(SalaryOverviewService salaries) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? filter, string? sort, int page = 1, CancellationToken cancellationToken = default)
    {
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 100)];
        var isAdmin = User.IsInRole(AppRoles.Admin);
        var parsedFilter = Enum.TryParse<SalaryFilter>(filter, ignoreCase: true, out var f) && Enum.IsDefined(f) ? f : SalaryFilter.All;
        if (!isAdmin && parsedFilter == SalaryFilter.NeedsReview)
        {
            parsedFilter = SalaryFilter.All; // Admin-only filter
        }

        var (sortBy, descending, sortValue) = SalarySortOptions.Parse(sort);
        var query = new SalaryQuery(search, parsedFilter, sortBy, descending, page);

        if (isAdmin)
        {
            var (result, totals) = await salaries.ListForAdminAsync(query, cancellationToken);
            return View(new SalariesPageViewModel(result.Items.Select(r => r.Row).ToList(), search, parsedFilter, sortValue, result.Page, result.TotalPages,
                result.TotalCount, new AdminSalariesViewModel(result.Items.ToDictionary(r => r.Row.PersonId), totals)));
        }

        var managerResult = await salaries.ListAsync(query, cancellationToken);
        return View(new SalariesPageViewModel(managerResult.Items, search, parsedFilter, sortValue, managerResult.Page, managerResult.TotalPages,
            managerResult.TotalCount, Admin: null));
    }
}
