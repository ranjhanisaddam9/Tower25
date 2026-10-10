using HR.Infrastructure;
using HR.Infrastructure.Security;

namespace HR.Web.ViewModels;

/// <param name="RetentionDays">0 = keep forever (the default).</param>
public sealed record AuditPageViewModel(AuditQuery Query, PagedResult<AuditRow> Rows, AuditFilterOptions Options, int RetentionDays)
{
    public bool HasFilter => Query.From is not null || Query.To is not null || Query.UserId is not null || Query.EventId is not null || Query.EntityType is not null;

    public Dictionary<string, string> Route(int page)
    {
        var route = new Dictionary<string, string> { ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (Query.From is { } f) route["from"] = ReportDisplay.Iso(f);
        if (Query.To is { } t) route["to"] = ReportDisplay.Iso(t);
        if (Query.UserId is { } u) route["user"] = u;
        if (Query.EventId is { } e) route["eventId"] = e.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (Query.EntityType is { } en) route["entity"] = en;
        return route;
    }
}
