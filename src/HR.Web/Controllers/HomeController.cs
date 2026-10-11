using HR.Domain.Payroll;
using HR.Domain.Time;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Pay;
using HR.Infrastructure.Payroll;
using HR.Infrastructure.People;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Reports;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

public class HomeController(IClock clock, PersonService people, ExchangeRateService rates, PayRecordService pay, AbsenceService absences, PayrollService payroll, HR.Infrastructure.Invoices.InvoiceService invoices, ReportService reports, HR.Infrastructure.Security.AuditQueryService audit, HR.Infrastructure.Backups.BackupStatusService backups) : Controller
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
            await absences.GetDashboardAsync(cancellationToken),
            await payroll.DashboardAsync(cancellationToken),
            User.IsInRole(AppRoles.Admin) ? await payroll.DashboardAdminAsync(cancellationToken) : null,
            User.IsInRole(AppRoles.Admin) ? await invoices.DashboardAsync(cancellationToken) : null,
            await TrendAsync(cancellationToken),
            User.IsInRole(AppRoles.Admin) ? await audit.AlertsAsync(cancellationToken) : null,
            User.IsInRole(AppRoles.Admin) ? await backups.GetWarningAsync(cancellationToken) : null);

        return View(model);
    }

    /// <summary>Net pay of the last finalized periods; owner earnings only for Admins (never queried for Managers).</summary>
    private async Task<PayrollTrendViewModel> TrendAsync(CancellationToken cancellationToken)
    {
        if (User.IsInRole(AppRoles.Admin))
        {
            var admin = (await reports.AdminPayrollHistoryAsync(ReportService.TrendPeriods, cancellationToken)).Reverse().ToList();
            return new PayrollTrendViewModel(admin.Select(a => a.Row).ToList(), admin.Select(a => a.OwnerEarningUsd).ToList());
        }

        return new PayrollTrendViewModel((await reports.PayrollHistoryAsync(ReportService.TrendPeriods, cancellationToken)).Reverse().ToList(), null);
    }
}
