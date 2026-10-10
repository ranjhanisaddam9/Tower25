using HR.Domain.Time;
using HR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HR.Infrastructure.Security;

/// <param name="From">Asia/Karachi date, inclusive.</param>
/// <param name="To">Asia/Karachi date, inclusive.</param>
public sealed record AuditQuery(DateOnly? From, DateOnly? To, string? UserId, int? EventId, string? EntityType, int Page, int PageSize = AuditQueryService.DefaultPageSize);

public sealed record AuditRow(long Id, DateTimeOffset At, string? ActorUserId, string? ActorName, string? ActorIp, int EventId, string EventName, string? EntityType, string? EntityId, string Summary);

public sealed record AuditFilterOptions(IReadOnlyList<(string Id, string Name)> Users, IReadOnlyList<string> EntityTypes);

/// <summary>The dashboard alert (Admin): suspicious activity in the last 24 hours.</summary>
public sealed record SecurityAlerts(int FailedLogins, int Lockouts, int TwoFactorFailures, int PayrollReopens)
{
    public const int FailedLoginThreshold = 10;
    public const int TwoFactorFailureThreshold = 5;

    public bool Any => FailedLogins >= FailedLoginThreshold || Lockouts > 0 || TwoFactorFailures >= TwoFactorFailureThreshold || PayrollReopens > 0;
}

/// <summary>Reads the audit trail for the Admin audit page, its export and the dashboard alert. Read-only.</summary>
public sealed class AuditQueryService(AppDbContext db, IClock clock)
{
    public const int DefaultPageSize = 50;

    public async Task<PagedResult<AuditRow>> ListAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        var filtered = Filter(query);
        var total = await filtered.CountAsync(cancellationToken);
        var page = PagedResult<AuditRow>.ClampPage(query.Page, total, query.PageSize);
        var rows = await Project(filtered.OrderByDescending(a => a.Id).Skip((page - 1) * query.PageSize).Take(query.PageSize)).ToListAsync(cancellationToken);
        return new PagedResult<AuditRow>(rows, page, query.PageSize, total);
    }

    /// <summary>Every matching row, newest first (capped), for the Excel export.</summary>
    public async Task<IReadOnlyList<AuditRow>> ExportAsync(AuditQuery query, CancellationToken cancellationToken = default) =>
        await Project(Filter(query).OrderByDescending(a => a.Id).Take(Exports.ExportLimits.MaxRows)).ToListAsync(cancellationToken);

    public async Task<AuditFilterOptions> FilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        var users = await db.AuditLog.AsNoTracking()
            .Where(a => a.ActorUserId != null)
            .GroupBy(a => a.ActorUserId!)
            .Select(g => new { Id = g.Key, Name = g.Max(a => a.ActorName) })
            .ToListAsync(cancellationToken);
        var types = await db.AuditLog.AsNoTracking().Where(a => a.EntityType != null).Select(a => a.EntityType!).Distinct().OrderBy(t => t).ToListAsync(cancellationToken);
        return new AuditFilterOptions(users.Select(u => (u.Id, u.Name ?? u.Id)).OrderBy(u => u.Item2, StringComparer.CurrentCultureIgnoreCase).ToList(), types);
    }

    public async Task<SecurityAlerts> AlertsAsync(CancellationToken cancellationToken = default)
    {
        var since = clock.UtcNow.AddHours(-24);
        var counts = await db.AuditLog.AsNoTracking()
            .Where(a => a.At >= since && (a.EventId == AuditEvents.LoginFailed.Id || a.EventId == AuditEvents.LockedOut.Id
                || a.EventId == AuditEvents.TwoFactorFailed.Id || a.EventId == AuditEvents.PayrollReopened.Id))
            .GroupBy(a => a.EventId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        int Count(AuditEvent e) => counts.GetValueOrDefault(e.Id);
        return new SecurityAlerts(Count(AuditEvents.LoginFailed), Count(AuditEvents.LockedOut), Count(AuditEvents.TwoFactorFailed), Count(AuditEvents.PayrollReopened));
    }

    private IQueryable<AuditLogEntry> Filter(AuditQuery query)
    {
        var rows = db.AuditLog.AsNoTracking();
        if (query.From is { } from)
        {
            var start = PakistanTime.StartOfDayUtc(from);
            rows = rows.Where(a => a.At >= start);
        }

        if (query.To is { } to)
        {
            var end = PakistanTime.StartOfDayUtc(to.AddDays(1));
            rows = rows.Where(a => a.At < end);
        }

        if (!string.IsNullOrWhiteSpace(query.UserId))
        {
            rows = rows.Where(a => a.ActorUserId == query.UserId);
        }

        if (query.EventId is { } eventId)
        {
            rows = rows.Where(a => a.EventId == eventId);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            rows = rows.Where(a => a.EntityType == query.EntityType);
        }

        return rows;
    }

    private static IQueryable<AuditRow> Project(IQueryable<AuditLogEntry> rows) =>
        rows.Select(a => new AuditRow(a.Id, a.At, a.ActorUserId, a.ActorName, a.ActorIp, a.EventId, a.EventName, a.EntityType, a.EntityId, a.Summary));
}
