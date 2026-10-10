using HR.Domain.Payroll;
using HR.Domain.Time;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

public class HomeController(IClock clock, PersonService people, ExchangeRateService rates, PayRecordService pay, AbsenceService absences) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var current = PayPeriod.For(today);
        var next = current.Next();
        var counts = await people.GetCountsAsync(cancellationToken);
        var rate = await rates.GetOverviewAsync(cancellationToken);

        // Admin-only figure: not even queried for Managers.
        var noPaySetup = await pay.CountActiveWithoutPaySetupAsync(cancellationToken);
        int? pendingReviews = User.IsInRole(AppRoles.Admin) ? await pay.CountPendingReviewsAsync(cancellationToken) : null;
        int? notAssigned = User.IsInRole(AppRoles.Admin)
            ? await people.CountActiveWithoutHireSourceAsync(cancellationToken)
            : null;

        var model = new DashboardViewModel(
            today,
            new PeriodSummaryViewModel(current.Start, current.End, current.WorkingDayCount),
            new PeriodSummaryViewModel(next.Start, next.End, next.WorkingDayCount),
            new PeopleTileViewModel(counts.Active, counts.Employees, counts.Internees),
            new RateTileViewModel(
                rate.Current?.UsdToPkr,
                rate.Current?.EffectiveFrom,
                rate.Current is { } currentRate ? RateChangeViewModel.Between(rate.Previous?.UsdToPkr, currentRate.UsdToPkr) : null),
            noPaySetup,
            pendingReviews,
            notAssigned,
            await absences.GetDashboardAsync(cancellationToken));

        return View(model);
    }
}
