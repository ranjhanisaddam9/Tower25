using System.Globalization;
using HR.Infrastructure.Exports;
using HR.Infrastructure.Security;
using HR.Web.Exports;
using HR.Web.Formatting;
using HR.Web.Security;
using HR.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Controllers;

/// <summary>The audit trail (M10): Admin only, read-only, filterable, exportable.</summary>
[Authorize(Policy = Policies.AdminOnly)]
[Route("admin/audit")]
public class AuditController(AuditQueryService audit, Downloads downloads, IConfiguration configuration) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(DateOnly? from, DateOnly? to, string? user, int? eventId, string? entity, int page = 1, CancellationToken cancellationToken = default)
    {
        var query = Query(from, to, user, eventId, entity, page);
        var result = await audit.ListAsync(query, cancellationToken);
        var options = await audit.FilterOptionsAsync(cancellationToken);
        var retention = configuration.GetValue<int?>(ServerCommands.RetentionKey) ?? 0;
        return View(new AuditPageViewModel(query, result, options, retention));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(DateOnly? from, DateOnly? to, string? user, int? eventId, string? entity, CancellationToken cancellationToken)
    {
        var query = Query(from, to, user, eventId, entity, 1);
        var rows = await audit.ExportAsync(query, cancellationToken);
        var filters = new List<ExportFilter>
        {
            new("From", query.From is { } f ? DisplayFormat.Date(f) : "any"),
            new("To", query.To is { } t ? DisplayFormat.Date(t) : "any"),
            new("User", query.UserId ?? "any"),
            new("Event", query.EventId?.ToString(CultureInfo.InvariantCulture) ?? "any"),
            new("Entity", query.EntityType ?? "any"),
        };
        return await downloads.SendAsync(this, ExcelExports.AuditLog(downloads.Context(User, "Audit log", filters), rows, downloads.Now), "Audit log", filters);
    }

    private static AuditQuery Query(DateOnly? from, DateOnly? to, string? user, int? eventId, string? entity, int page) =>
        new(from, to,
            string.IsNullOrWhiteSpace(user) ? null : user.Trim()[..Math.Min(user.Trim().Length, 450)],
            eventId is { } e && AuditEvents.All.Any(a => a.Id == e) ? e : null,
            string.IsNullOrWhiteSpace(entity) ? null : entity.Trim()[..Math.Min(entity.Trim().Length, 40)],
            page);
}
