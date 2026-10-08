using HR.Domain.Payroll;
using HR.Web.Infrastructure;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>Developer-only pages. Every action returns 404 outside the Development environment.</summary>
[Route("dev")]
public class DevController(IWebHostEnvironment environment) : Controller
{
    [HttpGet("styleguide")]
    public IActionResult Styleguide()
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        const decimal rate = 280m;
        var rows = new List<StyleguideMoneyRow>
        {
            Row("Ayesha Siddiqui", "Software Engineer", "Active", 11, 150.00m, rate),
            Row("Bilal Ahmed", "QA Engineer", "Active", 6, 81.82m, rate),
            Row("Hira Malik", "Intern, Design", "On leave", 10, 143.18m, rate),
            Row("Usman Tariq", "Support Lead", "Leaving", 4, 54.55m, rate),
        };

        return View(new StyleguideViewModel(rows));
    }

    /// <summary>Demonstrates the POST-redirect-GET toast flow (and antiforgery validation).</summary>
    [HttpPost("styleguide/toast")]
    public IActionResult Toast(string kind)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }

        if (kind == "error")
        {
            TempData.ToastError("Something went wrong. This is a sample error toast.");
        }
        else
        {
            TempData.ToastSuccess("Saved. This is a sample success toast.");
        }

        return RedirectToAction(nameof(Styleguide));
    }

    private static StyleguideMoneyRow Row(string name, string designation, string status, int days, decimal payUsd, decimal rate) =>
        new(name, designation, status, days, payUsd, Money.RoundPkr(payUsd * rate));
}
