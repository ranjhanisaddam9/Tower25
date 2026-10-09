using HR.Domain.Payroll;
using HR.Domain.Time;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

public class HomeController(IClock clock, PersonService people) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var current = PayPeriod.For(today);
        var next = current.Next();
        var counts = await people.GetCountsAsync(cancellationToken);

        // Admin-only figure: not even queried for Managers.
        int? notAssigned = User.IsInRole(AppRoles.Admin)
            ? await people.CountActiveWithoutHireSourceAsync(cancellationToken)
            : null;

        var model = new DashboardViewModel(
            today,
            new PeriodSummaryViewModel(current.Start, current.End, current.WorkingDayCount),
            new PeriodSummaryViewModel(next.Start, next.End, next.WorkingDayCount),
            new PeopleTileViewModel(counts.Active, counts.Employees, counts.Internees),
            notAssigned);

        return View(model);
    }
}
