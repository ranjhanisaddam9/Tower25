using System.Security.Claims;
using HR.Domain.Time;
using HR.Infrastructure.Exports;
using HR.Infrastructure.Identity;
using HR.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace HR.Web.Exports;

/// <summary>A finished download: bytes, a safe ASCII file name, its content type and how many data rows it holds.</summary>
public sealed record ExportFile(byte[] Content, string FileName, string ContentType, int RowCount);

public static class ExportContentTypes
{
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Pdf = "application/pdf";
}

/// <summary>
/// Sends exports (M9): attachment with a safe file name, <c>Cache-Control: no-store</c> and <c>nosniff</c>, and an audit
/// event (1700) with the user, report, format, filters and row count. Never row contents; a search term is logged only
/// as "(given)", because people search can match phone numbers.
/// </summary>
public sealed class Downloads(IClock clock, ILoggerFactory loggerFactory)
{
    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    /// <summary>Who and when, for the About sheet.</summary>
    public ExportContext Context(ClaimsPrincipal user, string report, IReadOnlyList<ExportFilter> filters) =>
        new(report, user.FindFirstValue(AppClaimTypes.FullName) ?? user.Identity?.Name ?? "unknown", clock.UtcNow, filters);

    public DateTimeOffset Now => clock.UtcNow;

    public FileContentResult Send(ControllerBase controller, ExportFile file, string report, IReadOnlyList<ExportFilter> filters)
    {
        var headers = controller.Response.Headers;
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers.XContentTypeOptions = "nosniff";

        var actor = controller.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
        var format = file.ContentType == ExportContentTypes.Pdf ? "pdf" : "xlsx";
        SecurityLog.Exported(_log, actor, report, format, file.RowCount, Describe(filters));
        return controller.File(file.Content, file.ContentType, file.FileName);
    }

    public static string Describe(IReadOnlyList<ExportFilter> filters) =>
        filters.Count == 0 ? "none" : string.Join("; ", filters.Select(f => $"{f.Name}={(f.Name == SearchFilterName ? "(given)" : f.Value)}"));

    public const string SearchFilterName = "Search";
}

/// <summary>
/// Builds an Export button's link from the filters the page actually applied (its parsed view model values, never the raw
/// query string), so the file matches the page and a Manager's page never echoes an Admin-only parameter.
/// </summary>
public static class ExportLinks
{
    public static string For(string path, params (string Key, object? Value)[] filters) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(path, filters
            .Where(f => f.Value is not null && !(f.Value is string s && s.Length == 0))
            .Select(f => new KeyValuePair<string, string?>(f.Key, f.Value switch
            {
                DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToString(f.Value, System.Globalization.CultureInfo.InvariantCulture),
            })));
}
