using HR.Domain.Settings;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Settings;

public enum SettingsResultStatus
{
    Success,
    Invalid,
    Conflict,
}

public sealed record SettingsResult(SettingsResultStatus Status, IReadOnlyList<SettingsError>? Errors = null, IReadOnlyList<string>? ChangedFields = null)
{
    public bool Succeeded => Status == SettingsResultStatus.Success;
}

/// <summary>The single settings row (M8). Admin only (the controller enforces it). Audits changed field names, never values.</summary>
public sealed class SettingsService(AppDbContext db, IClock clock, ILoggerFactory loggerFactory)
{
    public const string ConflictMessage = "Someone else saved the settings while you were editing. Your changes were not saved; review the current values and try again.";

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    /// <summary>The settings (read-only). The row is created by the migration; an empty table yields the defaults.</summary>
    public async Task<AppSettings> GetAsync(CancellationToken cancellationToken = default) =>
        await db.Set<AppSettings>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == AppSettings.SingletonId, cancellationToken)
        ?? AppSettings.CreateDefault();

    public async Task<SettingsResult> UpdateAsync(SettingsInput input, byte[] rowVersion, string actorId, CancellationToken cancellationToken = default)
    {
        var normalized = SettingsRules.Normalize(input, out var errors);
        if (normalized is null)
        {
            return new SettingsResult(SettingsResultStatus.Invalid, errors);
        }

        var settings = await db.Set<AppSettings>().SingleOrDefaultAsync(s => s.Id == AppSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = AppSettings.CreateDefault();
            db.Add(settings);
        }
        else if (!settings.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new SettingsResult(SettingsResultStatus.Conflict);
        }
        else
        {
            db.Entry(settings).Property(s => s.RowVersion).OriginalValue = rowVersion;
        }

        var changed = settings.Apply(normalized, actorId, clock.UtcNow);
        if (changed.Count == 0)
        {
            db.ChangeTracker.Clear();
            return new SettingsResult(SettingsResultStatus.Success, ChangedFields: changed);
        }

        // Field names only, never values (bank details are in here).
        db.Audit(AuditEvents.SettingsChanged, actorId, "Settings", AppSettings.SingletonId, "Changed: " + string.Join(", ", changed));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new SettingsResult(SettingsResultStatus.Conflict);
        }

        SecurityLog.SettingsChanged(_log, actorId, string.Join(", ", changed));
        return new SettingsResult(SettingsResultStatus.Success, ChangedFields: changed);
    }
}
