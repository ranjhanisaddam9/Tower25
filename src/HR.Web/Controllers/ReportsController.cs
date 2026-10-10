using HR.Domain.Reports;
using HR.Domain.Time;
using HR.Infrastructure.Absences;
using HR.Infrastructure.Exports;
using HR.Infrastructure.Identity;
using HR.Infrastructure.People;
using HR.Infrastructure.Reports;
using HR.Web.Exports;
using HR.Web.Formatting;
using HR.Web.Infrastructure;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>
/// Reports (M9 Part E) for Managers and Admins. Billing figures (invoice, owner earning, billed amounts) are loaded only
/// for Admins, through separate service methods; Managers' pages and exports are built from pay-only rows.
/// </summary>
[Authorize(Policy = Policies.ManagerOrAdmin)]
[Route("reports")]
public class ReportsController(ReportService reports, AbsenceService absences, Downloads downloads, IClock clock) : Controller
{
    private bool IsAdmin => User.IsInRole(AppRoles.Admin);

    [HttpGet("")]
    public IActionResult Index() => View();

    // ===================== Payroll history =====================

    [HttpGet("payroll-history")]
    public async Task<IActionResult> PayrollHistory(CancellationToken cancellationToken)
    {
        if (IsAdmin)
        {
            var admin = await reports.AdminPayrollHistoryAsync(cancellationToken: cancellationToken);
            var rows = admin.Select(a => a.Row).ToList();
            return View(new PayrollHistoryViewModel(rows, admin, PayrollHistoryViewModel.NetPay(rows), PayrollHistoryViewModel.Earnings(admin)));
        }

        var history = await reports.PayrollHistoryAsync(cancellationToken: cancellationToken);
        return View(new PayrollHistoryViewModel(history, null, PayrollHistoryViewModel.NetPay(history), null));
    }

    [HttpGet("payroll-history/export")]
    public async Task<IActionResult> PayrollHistoryExport(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExportFilter> filters = [new("Payrolls", "All finalized")];
        var context = downloads.Context(User, "Payroll history", filters);
        var file = IsAdmin
            ? ExcelExports.PayrollHistoryAdmin(context, await reports.AdminPayrollHistoryAsync(cancellationToken: cancellationToken), clock.Today)
            : ExcelExports.PayrollHistory(context, await reports.PayrollHistoryAsync(cancellationToken: cancellationToken), clock.Today);
        return await downloads.SendAsync(this, file, "Payroll history", filters);
    }

    // ===================== Salary changes =====================

    [HttpGet("salary-changes")]
    public async Task<IActionResult> SalaryChanges(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = DefaultYear(from, to);
        if (end < start)
        {
            return View(new SalaryChangesViewModel(start, end, [], IsAdmin, ReportRange.OrderMessage));
        }

        var rows = IsAdmin ? await reports.AdminSalaryChangesAsync(start, end, cancellationToken) : await reports.SalaryChangesAsync(start, end, cancellationToken);
        return View(new SalaryChangesViewModel(start, end, rows, IsAdmin, null));
    }

    [HttpGet("salary-changes/export")]
    public async Task<IActionResult> SalaryChangesExport(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = DefaultYear(from, to);
        if (end < start)
        {
            TempData.ToastError(ReportRange.OrderMessage);
            return RedirectToAction(nameof(SalaryChanges));
        }

        IReadOnlyList<ExportFilter> filters = [new("Effective between", DisplayFormat.DateRange(start, end))];
        var context = downloads.Context(User, "Salary changes", filters);
        var file = IsAdmin
            ? ExcelExports.SalaryChangesAdmin(context, await reports.AdminSalaryChangesAsync(start, end, cancellationToken), start, end)
            : ExcelExports.SalaryChanges(context, await reports.SalaryChangesAsync(start, end, cancellationToken), start, end);
        return await downloads.SendAsync(this, file, "Salary changes", filters);
    }

    // ===================== Absence summary =====================

    [HttpGet("absences")]
    public async Task<IActionResult> Absences(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = DefaultYear(from, to);
        if (ReportRange.Validate(start, end) is { } problem)
        {
            return View(new AbsenceSummaryViewModel(start, end, null, problem));
        }

        return View(new AbsenceSummaryViewModel(start, end, await SummaryAsync(start, end, cancellationToken), null));
    }

    [HttpGet("absences/export")]
    public async Task<IActionResult> AbsencesExport(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (start, end) = DefaultYear(from, to);
        if (ReportRange.Validate(start, end) is { } problem)
        {
            TempData.ToastError(problem);
            return RedirectToAction(nameof(Absences));
        }

        IReadOnlyList<ExportFilter> filters = [new("Dates", DisplayFormat.DateRange(start, end)), new("People", "All")];
        var file = ExcelExports.AbsenceSummary(downloads.Context(User, "Absence summary", filters), await SummaryAsync(start, end, cancellationToken));
        return await downloads.SendAsync(this, file, "Absence summary", filters);
    }

    // ===================== Headcount =====================

    [HttpGet("headcount")]
    public async Task<IActionResult> Headcount(CancellationToken cancellationToken) =>
        View(new HeadcountViewModel(await reports.HeadcountAsync(cancellationToken)));

    [HttpGet("headcount/export")]
    public async Task<IActionResult> HeadcountExport(CancellationToken cancellationToken)
    {
        IReadOnlyList<ExportFilter> filters = [new("Months", "Last 12")];
        var file = ExcelExports.Headcount(downloads.Context(User, "Headcount", filters), await reports.HeadcountAsync(cancellationToken), clock.Today);
        return await downloads.SendAsync(this, file, "Headcount", filters);
    }

    // ===================== Helpers =====================

    /// <summary>Defaults to the 12 calendar months ending with this one.</summary>
    private (DateOnly From, DateOnly To) DefaultYear(DateOnly? from, DateOnly? to)
    {
        var today = clock.Today;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        return (from ?? thisMonth.AddMonths(-11), to ?? thisMonth.AddMonths(1).AddDays(-1));
    }

    private Task<AbsenceRange> SummaryAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        absences.RangeAsync(new AbsenceRangeQuery(from, to, null, null, PaidStatusFilter.All, PersonStatusFilter.All), cancellationToken);
}
