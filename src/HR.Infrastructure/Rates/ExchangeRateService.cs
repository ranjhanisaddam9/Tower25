using HR.Domain.Rates;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Rates;

/// <summary>A rate as used by other features: the value and the entry it came from.</summary>
public sealed record RateSnapshot(int Id, DateOnly EffectiveFrom, decimal UsdToPkr, string? Note);

/// <summary>Rate lookups for other features (dashboard now; payroll proposes the period-end rate in M7).</summary>
public interface IExchangeRateService
{
    /// <summary>The entry with the latest EffectiveFrom on or before <paramref name="date"/>, or null.</summary>
    Task<RateSnapshot?> GetRateForAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>The rate for today (Asia/Karachi).</summary>
    Task<RateSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>The current rate and the entry before it (for "change vs previous").</summary>
public sealed record RateOverview(RateSnapshot? Current, RateSnapshot? Previous);

public sealed record RateRow(int Id, DateOnly EffectiveFrom, decimal UsdToPkr, string? Note, string? AddedBy, decimal? PreviousUsdToPkr);

public sealed record RateDetails(int Id, DateOnly EffectiveFrom, decimal UsdToPkr, string? Note, byte[] RowVersion);

public sealed record RateInput(DateOnly? EffectiveFrom, decimal? UsdToPkr, string? Note);

public sealed record RateError(string Field, string Message);

/// <summary>The entry a new or edited rate would follow, and how far it moves from it.</summary>
public sealed record LargeChange(DateOnly PreviousEffectiveFrom, decimal PreviousUsdToPkr, decimal PercentChange);

public enum RateResultStatus
{
    Success,
    NotFound,
    Invalid,
    NeedsConfirmation,
    Conflict,
}

public sealed record RateResult(
    RateResultStatus Status,
    int? Id = null,
    IReadOnlyList<RateError>? Errors = null,
    LargeChange? LargeChange = null,
    RateDetails? Current = null)
{
    public bool Succeeded => Status == RateResultStatus.Success;

    public static RateResult Invalid(string field, string message) => new(RateResultStatus.Invalid, Errors: [new RateError(field, message)]);
}

/// <summary>Exchange-rate history (SPEC §2): lookups, list, chart data and audited corrections.</summary>
public sealed class ExchangeRateService(AppDbContext db, IClock clock, ILoggerFactory loggerFactory) : IExchangeRateService
{
    public const int DefaultPageSize = 20;
    public const int ChartEntries = 12;
    public const string DuplicateDateMessage = "There is already a rate effective from this date. Edit that entry instead.";

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    public async Task<RateSnapshot?> GetRateForAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        await db.ExchangeRates.AsNoTracking()
            .InEffectOn(date)
            .Select(r => new RateSnapshot(r.Id, r.EffectiveFrom, r.UsdToPkr, r.Note))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<RateSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        GetRateForAsync(clock.Today, cancellationToken);

    public async Task<RateOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentAsync(cancellationToken);
        if (current is null)
        {
            return new RateOverview(null, null);
        }

        var previous = await GetRateForAsync(current.EffectiveFrom.AddDays(-1), cancellationToken);
        return new RateOverview(current, previous);
    }

    /// <summary>Newest first, each row with the rate of the entry before it.</summary>
    public async Task<PagedResult<RateRow>> ListAsync(int page, int pageSize = DefaultPageSize, CancellationToken cancellationToken = default)
    {
        var total = await db.ExchangeRates.CountAsync(cancellationToken);
        page = PagedResult<RateRow>.ClampPage(page, total, pageSize);

        // One extra (older) row so the last row on the page also gets its "change vs previous".
        var rows = await (
                from r in db.ExchangeRates.AsNoTracking()
                join u in db.Users on r.CreatedByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby r.EffectiveFrom descending
                select new { r.Id, r.EffectiveFrom, r.UsdToPkr, r.Note, AddedBy = u == null ? null : u.FullName })
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var items = rows
            .Take(pageSize)
            .Select((r, i) => new RateRow(r.Id, r.EffectiveFrom, r.UsdToPkr, r.Note, r.AddedBy, i + 1 < rows.Count ? rows[i + 1].UsdToPkr : null))
            .ToList();
        return new PagedResult<RateRow>(items, page, pageSize, total);
    }

    /// <summary>The last <see cref="ChartEntries"/> entries, oldest first.</summary>
    public async Task<IReadOnlyList<RateSnapshot>> GetChartEntriesAsync(CancellationToken cancellationToken = default)
    {
        var latest = await db.ExchangeRates.AsNoTracking()
            .OrderByDescending(r => r.EffectiveFrom)
            .Take(ChartEntries)
            .Select(r => new RateSnapshot(r.Id, r.EffectiveFrom, r.UsdToPkr, r.Note))
            .ToListAsync(cancellationToken);
        latest.Reverse();
        return latest;
    }

    public Task<RateDetails?> GetAsync(int id, CancellationToken cancellationToken = default) =>
        db.ExchangeRates.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new RateDetails(r.Id, r.EffectiveFrom, r.UsdToPkr, r.Note, r.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<RateResult> CreateAsync(RateInput input, bool largeChangeConfirmed, string actorId, CancellationToken cancellationToken = default)
    {
        var checkResult = await CheckAsync(input, exceptId: null, largeChangeConfirmed, cancellationToken);
        if (checkResult is not null)
        {
            return checkResult;
        }

        var rate = ExchangeRate.Create(input.EffectiveFrom!.Value, input.UsdToPkr!.Value, input.Note, actorId, clock.UtcNow);
        db.ExchangeRates.Add(rate);
        db.Audit(AuditEvents.ExchangeRateCreated, actorId, "ExchangeRate", () => rate.Id, $"Added {Iso(rate.EffectiveFrom)} = {Rate(rate.UsdToPkr)}");
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.ExchangeRateCreated(_log, actorId, rate.Id, rate.EffectiveFrom, rate.UsdToPkr);
        return new RateResult(RateResultStatus.Success, rate.Id);
    }

    public async Task<RateResult> UpdateAsync(
        int id,
        RateInput input,
        bool largeChangeConfirmed,
        byte[] rowVersion,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        var rate = await db.ExchangeRates.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rate is null)
        {
            return new RateResult(RateResultStatus.NotFound);
        }

        if (!rate.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return await ConflictAsync(id, cancellationToken);
        }

        var checkResult = await CheckAsync(input, exceptId: id, largeChangeConfirmed, cancellationToken);
        if (checkResult is not null)
        {
            return checkResult;
        }

        var (oldDate, oldRate) = (rate.EffectiveFrom, rate.UsdToPkr);
        db.Entry(rate).Property(r => r.RowVersion).OriginalValue = rowVersion;
        rate.Update(input.EffectiveFrom!.Value, input.UsdToPkr!.Value, input.Note, actorId, clock.UtcNow);
        db.Audit(AuditEvents.ExchangeRateEdited, actorId, "ExchangeRate", rate.Id,
            $"Edited {Iso(oldDate)} = {Rate(oldRate)} -> {Iso(rate.EffectiveFrom)} = {Rate(rate.UsdToPkr)}");

        try
        {
            if (await SaveAsync(cancellationToken) is { } failure)
            {
                return failure;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return await ConflictAsync(id, cancellationToken);
        }

        SecurityLog.ExchangeRateEdited(_log, actorId, rate.Id, oldDate, oldRate, rate.EffectiveFrom, rate.UsdToPkr);
        return new RateResult(RateResultStatus.Success, rate.Id);
    }

    public async Task<RateResult> DeleteAsync(int id, string actorId, CancellationToken cancellationToken = default)
    {
        var rate = await db.ExchangeRates.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rate is null)
        {
            return new RateResult(RateResultStatus.NotFound);
        }

        db.ExchangeRates.Remove(rate);
        db.Audit(AuditEvents.ExchangeRateDeleted, actorId, "ExchangeRate", id, $"Deleted {Iso(rate.EffectiveFrom)} = {Rate(rate.UsdToPkr)}");
        await db.SaveChangesAsync(cancellationToken);
        SecurityLog.ExchangeRateDeleted(_log, actorId, id, rate.EffectiveFrom, rate.UsdToPkr);
        return new RateResult(RateResultStatus.Success, id);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Rate(decimal rate) => rate.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Validation, duplicate date and the >5% rule. Null when the input may be saved.</summary>
    private async Task<RateResult?> CheckAsync(RateInput input, int? exceptId, bool largeChangeConfirmed, CancellationToken cancellationToken)
    {
        var errors = new List<RateError>();
        if (input.EffectiveFrom is null)
        {
            errors.Add(new RateError(nameof(RateInput.EffectiveFrom), "Enter the date the rate takes effect."));
        }

        if (input.UsdToPkr is null)
        {
            errors.Add(new RateError(nameof(RateInput.UsdToPkr), "Enter the rate."));
        }
        else if (ExchangeRateRules.RateError(input.UsdToPkr.Value) is { } rateError)
        {
            errors.Add(new RateError(nameof(RateInput.UsdToPkr), rateError));
        }

        if (input.Note is { } note && note.Trim().Length > ExchangeRateRules.NoteMaxLength)
        {
            errors.Add(new RateError(nameof(RateInput.Note), "The note can be at most 200 characters."));
        }

        if (input.EffectiveFrom is { } date
            && await db.ExchangeRates.AnyAsync(r => r.EffectiveFrom == date && r.Id != exceptId, cancellationToken))
        {
            errors.Add(new RateError(nameof(RateInput.EffectiveFrom), DuplicateDateMessage));
        }

        if (errors.Count > 0)
        {
            return new RateResult(RateResultStatus.Invalid, Errors: errors);
        }

        // Compare with the entry this one would follow (excluding itself when editing).
        var effectiveFrom = input.EffectiveFrom!.Value;
        var preceding = await db.ExchangeRates.AsNoTracking()
            .Where(r => r.EffectiveFrom < effectiveFrom && r.Id != exceptId)
            .OrderByDescending(r => r.EffectiveFrom)
            .Select(r => new { r.EffectiveFrom, r.UsdToPkr })
            .FirstOrDefaultAsync(cancellationToken);

        if (preceding is not null && !largeChangeConfirmed && ExchangeRateRules.IsLargeChange(preceding.UsdToPkr, input.UsdToPkr!.Value))
        {
            return new RateResult(RateResultStatus.NeedsConfirmation,
                LargeChange: new LargeChange(preceding.EffectiveFrom, preceding.UsdToPkr,
                    ExchangeRateRules.PercentChange(preceding.UsdToPkr, input.UsdToPkr.Value)));
        }

        return null;
    }

    private async Task<RateResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException
            && ex.InnerException is SqlException { Number: 2601 or 2627 } sql
            && sql.Message.Contains(ExchangeRateConfiguration.EffectiveFromIndex, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return RateResult.Invalid(nameof(RateInput.EffectiveFrom), DuplicateDateMessage);
        }
    }

    private async Task<RateResult> ConflictAsync(int id, CancellationToken cancellationToken)
    {
        var current = await GetAsync(id, cancellationToken);
        return current is null
            ? new RateResult(RateResultStatus.NotFound)
            : new RateResult(RateResultStatus.Conflict, id, Current: current);
    }
}
