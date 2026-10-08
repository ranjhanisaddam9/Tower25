using HR.Domain.Payroll;
using HR.Domain.Time;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

public class HomeController(IClock clock) : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        var today = clock.Today;
        var current = PayPeriod.For(today);
        var next = current.Next();

        var model = new DashboardViewModel(
            today,
            new PeriodSummaryViewModel(current.Start, current.End, current.WorkingDayCount),
            new PeriodSummaryViewModel(next.Start, next.End, next.WorkingDayCount));

        return View(model);
    }
}
