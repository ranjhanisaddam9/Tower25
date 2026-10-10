using System.Data;
using System.Globalization;
using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Payroll;

// ===================== Read models (Manager-safe unless named Admin) =====================

public sealed record PayrollRunRow(
    int Id,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    PayrollStatus Status,
    int People,
    decimal TotalNetPayPkr,
    DateTimeOffset? FinalizedAt,
    string? FinalizedBy);

/// <summary>Admin-only totals for a run. Never loaded for a Manager.</summary>
public sealed record PayrollAdminTotals(decimal InvoiceUsd, decimal OwnerEarningUsd, decimal OwnerEarningPkr, decimal BilledUsd);

public sealed record PayrollRunHeader(
    int Id,
    PayPeriod Period,
    int WorkingDays,
    PayrollStatus Status,
    decimal? ExchangeRate,
    int? ExchangeRateEntryId,
    DateOnly? ExchangeRateEntryDate,
    bool RateOverridden,
    string? RateNote,
    DateTimeOffset GeneratedAt,
    string? GeneratedBy,
    DateTimeOffset? FinalizedAt,
    string? FinalizedBy,
    bool IsLatestFinalized,
    byte[] RowVersion)
{
    public bool IsDraft => Status == PayrollStatus.Draft;
}

public sealed record PayrollEventRow(PayrollEventType Type, string? Detail, string? Actor, DateTimeOffset At);

/// <summary>A line as Managers see it: days and pay only.</summary>
public sealed record PayrollLineRow(
    int Id,
    int PersonId,
    string PersonCode,
    string PersonName,
    string Designation,
    PersonType PersonType,
    int WorkingDays,
    int EmployedWorkingDays,
    decimal UnpaidDays,
    decimal ExtraDays,
    decimal PayableDays,
    decimal? PayMonthlyAmount,
    PayCurrency? PayCurrency,
    decimal? PayPkr,
    decimal? PayUsd,
    decimal? AdjustmentsPkr,
    decimal? NetPayPkr,
    decimal? NetPayUsd,
    LineIssue? Issue,
    bool IsOrphaned,
    int AdjustmentCount);

/// <summary>Admin-only line columns, loaded separately.</summary>
public sealed record PayrollLineBilling(
    int LineId,
    HireSource? HireSource,
    decimal? BilledMonthlyUsd,
    decimal? CommissionPerPeriodUsd,
    decimal? SalaryPartUsd,
    decimal? CommissionUsd,
    decimal? BilledUsd,
    decimal? AdjustmentsUsd,
    decimal? InvoiceUsd,
    decimal? OwnerEarningUsd,
    decimal? OwnerEarningPkr);

public sealed record PayrollAdjustmentRow(
    int Id,
    AdjustmentType Type,
    decimal Amount,
    PayCurrency Currency,
    string? Note,
    decimal? AmountPkr,
    decimal? AmountUsd,
    string? AddedBy,
    byte[] RowVersion);

public sealed record PayrollLineAbsenceRow(DateOnly Date, AbsencePortion Portion, decimal PaidDays, decimal UnpaidDays);

public sealed record PayrollLineDetail(
    PayrollLineRow Line,
    string? ExtraDaysNote,
    IReadOnlyList<PayrollLineAbsenceRow> Absences,
    IReadOnlyList<PayrollAdjustmentRow> Adjustments);

public sealed record RegisterRow(int LineId, string PersonCode, string PersonName, string? BankName, string? Iban, decimal? NetPayPkr);

public sealed record PayrollDashboard(PayPeriod Period, int? RunId, PayrollStatus? Status);

/// <summary>Admin only: the latest finalized run and its totals.</summary>
public sealed record PayrollDashboardAdmin(int RunId, PayPeriod Period, decimal InvoiceUsd, decimal OwnerEarningUsd);

// ===================== Results =====================

public enum PayrollResultStatus
{
    Success,
    NotFound,
    Invalid,
    NotDraft,
    Refused,
    DataChanged,
    Conflict,
    Exists,
}

public sealed record PayrollError(string Field, string Message);

/// <summary>One value that differs between the stored draft and a fresh calculation.</summary>
/// <param name="AdminOnly">Billing values: shown to Admins only.</param>
public sealed record LineChange(string PersonCode, string PersonName, string Field, string Old, string New, bool AdminOnly);

public sealed record PayrollResult(
    PayrollResultStatus Status,
    int? Id = null,
    IReadOnlyList<PayrollError>? Errors = null,
    IReadOnlyList<LineChange>? Changes = null,
    int Count = 0)
{
    public bool Succeeded => Status == PayrollResultStatus.Success;

    public string? Message => Errors?.FirstOrDefault()?.Message;

    public static PayrollResult Invalid(string field, string message) => new(PayrollResultStatus.Invalid, Errors: [new PayrollError(field, message)]);

    public static PayrollResult Refused(string message) => new(PayrollResultStatus.Refused, Errors: [new PayrollError(string.Empty, message)]);
}

/// <summary>
/// The payroll workflow (SPEC §5–§6): generate a draft, regenerate, set the rate, extra days and adjustments, finalize
/// (after re-checking the draft against a fresh calculation inside a serializable transaction), reopen (Admin, latest
/// only) and delete drafts. Manager queries never select billing columns. Audit events 15xx never contain notes.
/// </summary>
public sealed class PayrollService(AppDbContext db, IClock clock, IExchangeRateService rates, ILoggerFactory loggerFactory)
{
    public const string NotDraftMessage = "This payroll is finalized, so it can't be changed. An administrator can reopen it.";
    public const string DataChangedMessage = "Data changed since this draft was calculated. The draft has been recalculated; review the changes and finalize again.";
    public const string IssuesMessage = "Some lines have problems. Fix them, then regenerate the draft.";
    public const string NoRateMessage = "Set the exchange rate before finalizing.";
    public const string EarlierDraftMessage = "An earlier period still has a draft payroll. Finalize or delete it first.";
    public const string OrphansMessage = "Some people are no longer employed in this period but still have extra days or adjustments. Remove them first.";
    public const string BeforeFinalizedMessage = "That period is before the latest finalized payroll, so it can't get a new payroll.";
    public const string ExistsMessage = "A payroll for that period already exists.";
    public const string ConflictMessage = "Someone else changed this item at the same time. Reload and try again.";
    public const string ReopenLatestOnlyMessage = "Only the latest finalized payroll can be reopened.";
    public const string ReopenReasonMessage = "Give a reason of at least 10 characters (at most 500).";
    public const string RateNoteMessage = "Add a note explaining the rate change.";
    public const string NoRateInHistoryMessage = "There is no exchange rate in effect on the period's last day. Enter a rate with a note instead.";

    public const int NoDeleteFinalizedErrorNumber = 51010;
    public const int FrozenErrorNumber = 51011;

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    // ===================== Queries =====================

    public async Task<IReadOnlyList<PayrollRunRow>> ListAsync(CancellationToken cancellationToken = default) =>
        await (
                from r in db.PayrollRuns.AsNoTracking()
                join u in db.Users on r.FinalizedByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby r.PeriodStart descending
                select new PayrollRunRow(
                    r.Id, r.PeriodStart, r.PeriodEnd, r.Status,
                    db.PayrollLines.Count(l => l.RunId == r.Id && !l.IsOrphaned),
                    db.PayrollLines.Where(l => l.RunId == r.Id && !l.IsOrphaned).Sum(l => l.NetPayPkr ?? 0m),
                    r.FinalizedAt, u == null ? null : u.FullName))
            .ToListAsync(cancellationToken);

    /// <summary>Admin only.</summary>
    public async Task<IReadOnlyDictionary<int, PayrollAdminTotals>> AdminTotalsAsync(CancellationToken cancellationToken = default) =>
        (await db.PayrollLines.AsNoTracking()
            .Where(l => !l.IsOrphaned)
            .GroupBy(l => l.RunId)
            .Select(g => new
            {
                RunId = g.Key,
                Invoice = g.Sum(l => l.InvoiceUsd ?? 0m),
                Earning = g.Sum(l => l.OwnerEarningUsd ?? 0m),
                EarningPkr = g.Sum(l => l.OwnerEarningPkr ?? 0m),
                Billed = g.Sum(l => l.BilledUsd ?? 0m),
            })
            .ToListAsync(cancellationToken))
        .ToDictionary(x => x.RunId, x => new PayrollAdminTotals(x.Invoice, x.Earning, x.EarningPkr, x.Billed));

    /// <summary>The earliest period after the latest finalized one, else the current period.</summary>
    public async Task<PayPeriod> DefaultPeriodAsync(CancellationToken cancellationToken = default)
    {
        var latest = await LatestFinalizedStartAsync(cancellationToken);
        return latest is { } start ? PayPeriod.For(start).Next() : PayPeriod.For(clock.Today);
    }

    public async Task<int?> RunIdForPeriodAsync(DateOnly periodStart, CancellationToken cancellationToken = default) =>
        await db.PayrollRuns.AsNoTracking().Where(r => r.PeriodStart == periodStart).Select(r => (int?)r.Id).SingleOrDefaultAsync(cancellationToken);

    public async Task<PayrollRunHeader?> GetRunAsync(int id, CancellationToken cancellationToken = default)
    {
        var row = await (
                from r in db.PayrollRuns.AsNoTracking()
                where r.Id == id
                join g in db.Users on r.GeneratedByUserId equals g.Id into generated
                from g in generated.DefaultIfEmpty()
                join f in db.Users on r.FinalizedByUserId equals f.Id into finalized
                from f in finalized.DefaultIfEmpty()
                join e in db.ExchangeRates on r.ExchangeRateEntryId equals e.Id into entries
                from e in entries.DefaultIfEmpty()
                select new
                {
                    r.Id, r.PeriodStart, r.Status, r.ExchangeRate, r.ExchangeRateEntryId, EntryDate = e == null ? (DateOnly?)null : e.EffectiveFrom,
                    r.RateOverridden, r.RateNote, r.GeneratedAt, GeneratedBy = g == null ? null : g.FullName,
                    r.FinalizedAt, FinalizedBy = f == null ? null : f.FullName, r.RowVersion,
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var period = PayPeriod.For(row.PeriodStart);
        var latest = await LatestFinalizedStartAsync(cancellationToken);
        return new PayrollRunHeader(row.Id, period, period.WorkingDayCount, row.Status, row.ExchangeRate, row.ExchangeRateEntryId, row.EntryDate,
            row.RateOverridden, row.RateNote, row.GeneratedAt, row.GeneratedBy, row.FinalizedAt, row.FinalizedBy,
            row.Status == PayrollStatus.Finalized && latest == row.PeriodStart, row.RowVersion);
    }

    public async Task<IReadOnlyList<PayrollEventRow>> EventsAsync(int runId, CancellationToken cancellationToken = default) =>
        await (
                from e in db.Set<PayrollRunEvent>().AsNoTracking()
                where e.RunId == runId
                join u in db.Users on e.ActorId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby e.At descending, e.Id descending
                select new PayrollEventRow(e.Type, e.Type == PayrollEventType.Reopened || e.Type == PayrollEventType.RateChanged ? e.Detail : null, u == null ? null : u.FullName, e.At))
            .ToListAsync(cancellationToken);

    /// <summary>Lines as Managers see them (no billing columns are selected).</summary>
    public async Task<IReadOnlyList<PayrollLineRow>> LinesAsync(int runId, CancellationToken cancellationToken = default) =>
        await LineRows(db.PayrollLines.AsNoTracking().Where(l => l.RunId == runId).OrderBy(l => l.PersonName).ThenBy(l => l.PersonCode))
            .ToListAsync(cancellationToken);

    /// <summary>Admin only: the billing columns of a run's lines.</summary>
    public async Task<IReadOnlyDictionary<int, PayrollLineBilling>> BillingAsync(int runId, int? lineId = null, CancellationToken cancellationToken = default) =>
        (await db.PayrollLines.AsNoTracking()
            .Where(l => l.RunId == runId && (lineId == null || l.Id == lineId))
            .Select(l => new PayrollLineBilling(l.Id, l.HireSource, l.BilledMonthlyUsd, l.CommissionPerPeriodUsd, l.SalaryPartUsd, l.CommissionUsd,
                l.BilledUsd, l.AdjustmentsUsd, l.InvoiceUsd, l.OwnerEarningUsd, l.OwnerEarningPkr))
            .ToListAsync(cancellationToken))
        .ToDictionary(b => b.LineId);

    public async Task<PayrollLineDetail?> GetLineAsync(int runId, int lineId, CancellationToken cancellationToken = default)
    {
        var line = await LineRows(db.PayrollLines.AsNoTracking().Where(l => l.RunId == runId && l.Id == lineId)).SingleOrDefaultAsync(cancellationToken);
        if (line is null)
        {
            return null;
        }

        var note = await db.PayrollLines.AsNoTracking().Where(l => l.Id == lineId).Select(l => l.ExtraDaysNote).SingleAsync(cancellationToken);
        var absences = await db.Set<PayrollLineAbsence>().AsNoTracking()
            .Where(a => a.LineId == lineId)
            .OrderBy(a => a.Date)
            .Select(a => new PayrollLineAbsenceRow(a.Date, a.Portion, a.PaidDays, a.UnpaidDays))
            .ToListAsync(cancellationToken);
        var adjustments = await (
                from a in db.PayrollAdjustments.AsNoTracking()
                where a.LineId == lineId
                join u in db.Users on a.CreatedByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby a.Id
                select new PayrollAdjustmentRow(a.Id, a.Type, a.Amount, a.Currency, a.Note, a.AmountPkr, a.AmountUsd, u == null ? null : u.FullName, a.RowVersion))
            .ToListAsync(cancellationToken);
        return new PayrollLineDetail(line, note, absences, adjustments);
    }

    public async Task<IReadOnlyList<PayrollLineDetail>> GetAllLineDetailsAsync(int runId, CancellationToken cancellationToken = default)
    {
        var ids = await db.PayrollLines.AsNoTracking().Where(l => l.RunId == runId && !l.IsOrphaned)
            .OrderBy(l => l.PersonName).ThenBy(l => l.PersonCode).Select(l => l.Id).ToListAsync(cancellationToken);
        var details = new List<PayrollLineDetail>();
        foreach (var id in ids)
        {
            details.Add((await GetLineAsync(runId, id, cancellationToken))!);
        }

        return details;
    }

    public async Task<IReadOnlyList<RegisterRow>> RegisterAsync(int runId, CancellationToken cancellationToken = default) =>
        await db.PayrollLines.AsNoTracking()
            .Where(l => l.RunId == runId && !l.IsOrphaned)
            .OrderBy(l => l.PersonName).ThenBy(l => l.PersonCode)
            .Select(l => new RegisterRow(l.Id, l.PersonCode, l.PersonName, l.BankName, l.Iban, l.NetPayPkr))
            .ToListAsync(cancellationToken);

    public async Task<PayrollDashboard> DashboardAsync(CancellationToken cancellationToken = default)
    {
        var period = PayPeriod.For(clock.Today);
        var run = await db.PayrollRuns.AsNoTracking().Where(r => r.PeriodStart == period.Start)
            .Select(r => new { r.Id, r.Status }).SingleOrDefaultAsync(cancellationToken);
        return new PayrollDashboard(period, run?.Id, run?.Status);
    }

    /// <summary>Admin only.</summary>
    public async Task<PayrollDashboardAdmin?> DashboardAdminAsync(CancellationToken cancellationToken = default)
    {
        var latest = await db.PayrollRuns.AsNoTracking().Where(r => r.Status == PayrollStatus.Finalized)
            .OrderByDescending(r => r.PeriodStart).Select(r => new { r.Id, r.PeriodStart }).FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
        {
            return null;
        }

        var totals = await db.PayrollLines.AsNoTracking().Where(l => l.RunId == latest.Id && !l.IsOrphaned)
            .GroupBy(l => l.RunId)
            .Select(g => new { Invoice = g.Sum(l => l.InvoiceUsd ?? 0m), Earning = g.Sum(l => l.OwnerEarningUsd ?? 0m) })
            .SingleOrDefaultAsync(cancellationToken);
        return new PayrollDashboardAdmin(latest.Id, PayPeriod.For(latest.PeriodStart), totals?.Invoice ?? 0m, totals?.Earning ?? 0m);
    }

    // ===================== Generate, regenerate, rate =====================

    public async Task<PayrollResult> GenerateAsync(DateOnly? periodStart, string actorId, CancellationToken cancellationToken = default)
    {
        var period = periodStart is { } start ? PayPeriod.For(start) : await DefaultPeriodAsync(cancellationToken);
        if (await RunIdForPeriodAsync(period.Start, cancellationToken) is { } existing)
        {
            return new PayrollResult(PayrollResultStatus.Exists, existing, [new PayrollError(string.Empty, ExistsMessage)]);
        }

        if (await LatestFinalizedStartAsync(cancellationToken) is { } latest && period.Start < latest)
        {
            return PayrollResult.Invalid("PeriodStart", BeforeFinalizedMessage);
        }

        var proposed = await rates.GetRateForAsync(period.End, cancellationToken);
        var run = PayrollRun.Create(period, proposed?.UsdToPkr, proposed?.Id, actorId, clock.UtcNow);
        db.PayrollRuns.Add(run);
        await ApplyAsync(run, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            var id = await RunIdForPeriodAsync(period.Start, cancellationToken);
            return new PayrollResult(PayrollResultStatus.Exists, id, [new PayrollError(string.Empty, ExistsMessage)]);
        }

        SecurityLog.PayrollGenerated(_log, actorId, run.Id, period.Start, run.Lines.Count(l => !l.IsOrphaned), RateText(run.ExchangeRate));
        return new PayrollResult(PayrollResultStatus.Success, run.Id, Count: run.Lines.Count);
    }

    /// <summary>Recomputes every line from current data. Extra days and adjustments stay with the person.</summary>
    public async Task<PayrollResult> RegenerateAsync(int runId, string actorId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        if (!run.IsDraft)
        {
            return new PayrollResult(PayrollResultStatus.NotDraft, runId, [new PayrollError(string.Empty, NotDraftMessage)]);
        }

        var changes = await ApplyAsync(run, cancellationToken);
        run.MarkRegenerated(actorId, clock.UtcNow);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.PayrollRegenerated(_log, actorId, run.Id, run.Lines.Count(l => !l.IsOrphaned),
            changes.Select(c => c.PersonCode).Distinct().Count(), run.Lines.Count(l => l.IsOrphaned));
        return new PayrollResult(PayrollResultStatus.Success, run.Id, Changes: changes);
    }

    /// <summary>
    /// Sets the run's rate: <paramref name="useHistory"/> takes the rate in effect on the period's last day; otherwise
    /// <paramref name="rate"/> overrides it. A note is required either way when a rate was already set. PKR values follow.
    /// </summary>
    public async Task<PayrollResult> SetRateAsync(int runId, bool useHistory, decimal? rate, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        if (!run.IsDraft)
        {
            return new PayrollResult(PayrollResultStatus.NotDraft, runId, [new PayrollError(string.Empty, NotDraftMessage)]);
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > PayrollRun.RateNoteMaxLength })
        {
            return PayrollResult.Invalid("RateNote", "The note can be at most 300 characters.");
        }

        decimal newRate;
        int? entryId = null;
        if (useHistory)
        {
            if (await rates.GetRateForAsync(run.PeriodEnd, cancellationToken) is not { } entry)
            {
                return PayrollResult.Invalid("Rate", NoRateInHistoryMessage);
            }

            newRate = entry.UsdToPkr;
            entryId = entry.Id;
        }
        else
        {
            if (rate is not { } value || value < 1m || value > 10_000m || value != Math.Round(value, 4))
            {
                return PayrollResult.Invalid("Rate", "Enter a rate between 1 and 10,000 with at most 4 decimals.");
            }

            if (trimmed is null)
            {
                return PayrollResult.Invalid("RateNote", RateNoteMessage);
            }

            newRate = value;
        }

        if (run.ExchangeRate is not null && trimmed is null)
        {
            return PayrollResult.Invalid("RateNote", RateNoteMessage);
        }

        var old = run.ExchangeRate;
        run.SetRate(newRate, entryId, trimmed, actorId, clock.UtcNow);
        await ApplyAsync(run, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.PayrollRateChanged(_log, actorId, run.Id, RateText(old), RateText(newRate), entryId is null);
        return new PayrollResult(PayrollResultStatus.Success, run.Id);
    }

    // ===================== Extra days and adjustments =====================

    public async Task<PayrollResult> SetExtraDaysAsync(int runId, int lineId, decimal? days, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        var (run, line, failure) = await LoadDraftLineAsync(runId, lineId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var value = days ?? 0m;
        if (value != 0m && ExtraDaysRules.Error(value) is { } error)
        {
            return PayrollResult.Invalid("ExtraDays", error);
        }

        if (note?.Trim() is { Length: > PayrollLine.ExtraDaysNoteMaxLength })
        {
            return PayrollResult.Invalid("ExtraDaysNote", "The note can be at most 300 characters.");
        }

        if (value != 0m && string.IsNullOrWhiteSpace(note))
        {
            return PayrollResult.Invalid("ExtraDaysNote", "List the dates worked in the note.");
        }

        var old = line!.ExtraDays;
        line.SetExtraDays(value, note);
        await ApplyAsync(run!, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } saveFailure)
        {
            return saveFailure;
        }

        SecurityLog.PayrollExtraDaysChanged(_log, actorId, run!.Id, lineId, line.PersonId, old, value);
        return new PayrollResult(PayrollResultStatus.Success, lineId);
    }

    public async Task<PayrollResult> AddAdjustmentAsync(int runId, int lineId, AdjustmentType? type, decimal? amount, PayCurrency? currency, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        var (run, line, failure) = await LoadDraftLineAsync(runId, lineId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        if (AdjustmentErrors(type, amount, currency, note) is { } invalid)
        {
            return invalid;
        }

        var adjustment = line!.AddAdjustment(type!.Value, amount!.Value, currency!.Value, note, actorId, clock.UtcNow);
        await ApplyAsync(run!, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } saveFailure)
        {
            return saveFailure;
        }

        SecurityLog.PayrollAdjustmentAdded(_log, actorId, run!.Id, lineId, adjustment.Id, Describe(adjustment.Type, adjustment.Amount, adjustment.Currency));
        return new PayrollResult(PayrollResultStatus.Success, adjustment.Id);
    }

    public async Task<PayrollResult> UpdateAdjustmentAsync(int runId, int lineId, int adjustmentId, AdjustmentType? type, decimal? amount, PayCurrency? currency, string? note, byte[] rowVersion, string actorId, CancellationToken cancellationToken = default)
    {
        var (run, line, failure) = await LoadDraftLineAsync(runId, lineId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var adjustment = line!.Adjustments.SingleOrDefault(a => a.Id == adjustmentId);
        if (adjustment is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        if (!adjustment.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new PayrollResult(PayrollResultStatus.Conflict, adjustmentId, [new PayrollError(string.Empty, ConflictMessage)]);
        }

        if (AdjustmentErrors(type, amount, currency, note) is { } invalid)
        {
            return invalid;
        }

        var old = Describe(adjustment.Type, adjustment.Amount, adjustment.Currency);
        db.Entry(adjustment).Property(a => a.RowVersion).OriginalValue = rowVersion;
        adjustment.Update(type!.Value, amount!.Value, currency!.Value, note, actorId, clock.UtcNow);
        await ApplyAsync(run!, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } saveFailure)
        {
            return saveFailure;
        }

        SecurityLog.PayrollAdjustmentEdited(_log, actorId, run!.Id, lineId, adjustmentId, old, Describe(adjustment.Type, adjustment.Amount, adjustment.Currency));
        return new PayrollResult(PayrollResultStatus.Success, adjustmentId);
    }

    public async Task<PayrollResult> DeleteAdjustmentAsync(int runId, int lineId, int adjustmentId, string actorId, CancellationToken cancellationToken = default)
    {
        var (run, line, failure) = await LoadDraftLineAsync(runId, lineId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var adjustment = line!.Adjustments.SingleOrDefault(a => a.Id == adjustmentId);
        if (adjustment is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        var old = Describe(adjustment.Type, adjustment.Amount, adjustment.Currency);
        line.RemoveAdjustment(adjustment);
        db.PayrollAdjustments.Remove(adjustment);
        await ApplyAsync(run!, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } saveFailure)
        {
            return saveFailure;
        }

        SecurityLog.PayrollAdjustmentDeleted(_log, actorId, run!.Id, lineId, adjustmentId, old);
        return new PayrollResult(PayrollResultStatus.Success, lineId);
    }

    // ===================== Finalize, reopen, delete =====================

    /// <summary>
    /// Finalizes a draft when it has no issues or orphans, has a rate, no earlier period is still a draft, and a fresh
    /// calculation inside a serializable transaction matches the stored draft exactly. When it doesn't match, the draft is
    /// recalculated, saved, and the per-line differences are returned (status <see cref="PayrollResultStatus.DataChanged"/>).
    /// </summary>
    public async Task<PayrollResult> FinalizeAsync(int runId, string actorId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var run = await LoadRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        if (!run.IsDraft)
        {
            return new PayrollResult(PayrollResultStatus.NotDraft, runId, [new PayrollError(string.Empty, NotDraftMessage)]);
        }

        string? refusal = null;
        if (run.ExchangeRate is null)
        {
            refusal = NoRateMessage;
        }
        else if (await db.PayrollRuns.AnyAsync(r => r.PeriodStart < run.PeriodStart && r.Status == PayrollStatus.Draft, cancellationToken))
        {
            refusal = EarlierDraftMessage;
        }
        else if (await LatestFinalizedStartAsync(cancellationToken) is { } latest && latest > run.PeriodStart)
        {
            refusal = BeforeFinalizedMessage;
        }
        else if (run.Lines.Any(l => l.IsOrphaned))
        {
            refusal = OrphansMessage;
        }
        else if (run.Lines.Any(l => l.Issue is not null))
        {
            refusal = IssuesMessage;
        }

        if (refusal is not null)
        {
            SecurityLog.PayrollFinalizeRefused(_log, actorId, run.Id, refusal);
            return PayrollResult.Refused(refusal) with { Id = run.Id };
        }

        // The stored draft must equal a fresh calculation from the data as it is now.
        var changes = await ApplyAsync(run, cancellationToken);
        if (changes.Count > 0)
        {
            run.MarkRegenerated(actorId, clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            SecurityLog.PayrollFinalizeRefused(_log, actorId, run.Id, "data changed since the draft was calculated");
            return new PayrollResult(PayrollResultStatus.DataChanged, run.Id, [new PayrollError(string.Empty, DataChangedMessage)], changes);
        }

        run.Finalize(actorId, clock.UtcNow);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new PayrollResult(PayrollResultStatus.Conflict, runId, [new PayrollError(string.Empty, ConflictMessage)]);
        }

        await transaction.CommitAsync(cancellationToken);
        SecurityLog.PayrollFinalized(_log, actorId, run.Id, run.PeriodStart, run.Lines.Count, run.Lines.Sum(l => l.NetPayPkr ?? 0m), run.ExchangeRate!.Value);
        return new PayrollResult(PayrollResultStatus.Success, run.Id);
    }

    /// <summary>Admin only (the controller enforces the policy). The latest finalized run goes back to Draft, with a reason.</summary>
    public async Task<PayrollResult> ReopenAsync(int runId, string? reason, string actorId, CancellationToken cancellationToken = default)
    {
        var run = await db.PayrollRuns.Include(r => r.Events).SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        if (run.Status != PayrollStatus.Finalized)
        {
            return PayrollResult.Refused("This payroll is not finalized.") with { Id = runId };
        }

        if (await LatestFinalizedStartAsync(cancellationToken) != run.PeriodStart)
        {
            return PayrollResult.Refused(ReopenLatestOnlyMessage) with { Id = runId };
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < PayrollRun.ReopenReasonMinLength or > PayrollRun.ReopenReasonMaxLength)
        {
            return PayrollResult.Invalid("Reason", ReopenReasonMessage) with { Id = runId };
        }

        run.Reopen(trimmed, actorId, clock.UtcNow);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.PayrollReopened(_log, actorId, run.Id, run.PeriodStart);
        return new PayrollResult(PayrollResultStatus.Success, run.Id);
    }

    public async Task<PayrollResult> DeleteAsync(int runId, string actorId, CancellationToken cancellationToken = default)
    {
        var run = await db.PayrollRuns.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return new PayrollResult(PayrollResultStatus.NotFound);
        }

        if (!run.IsDraft)
        {
            return PayrollResult.Refused("A finalized payroll can never be deleted.") with { Id = runId };
        }

        db.PayrollRuns.Remove(run);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.PayrollDeleted(_log, actorId, runId, run.PeriodStart);
        return new PayrollResult(PayrollResultStatus.Success, runId);
    }

    // ===================== Calculation =====================

    /// <summary>
    /// Recalculates every line of <paramref name="run"/> from current data and writes the results onto the (tracked) lines.
    /// Lines are created for newly eligible people; people no longer employed in the period keep an orphaned line only
    /// while it has extra days or adjustments. Returns what changed compared with the stored values.
    /// </summary>
    private async Task<List<LineChange>> ApplyAsync(PayrollRun run, CancellationToken cancellationToken)
    {
        var period = run.Period;
        var (monthStart, monthEnd) = (new DateOnly(period.Start.Year, period.Start.Month, 1), new DateOnly(period.End.Year, period.End.Month, DateTime.DaysInMonth(period.End.Year, period.End.Month)));

        // Everyone with an employment period overlapping the period, plus whoever already has a line.
        var existingIds = run.Lines.Select(l => l.PersonId).ToList();
        var candidates = await db.EmploymentPeriods.AsNoTracking()
            .Where(e => e.StartDate <= period.End && (e.EndDate == null || e.EndDate >= period.Start))
            .Select(e => e.PersonId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var ids = candidates.Union(existingIds).ToList();

        var people = await db.People.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Code, p.FullName, p.Designation, p.Type, p.HireSource, p.BankName, p.Iban })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var employment = (await db.EmploymentPeriods.AsNoTracking()
                .Where(e => ids.Contains(e.PersonId))
                .Select(e => new { e.PersonId, e.StartDate, e.EndDate })
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.PersonId)
            .ToDictionary(g => g.Key, g => g.Select(e => new EmploymentSpan(e.StartDate, e.EndDate)).ToList());
        var absences = (await db.Absences.AsNoTracking()
                .Where(a => ids.Contains(a.PersonId) && a.Date >= monthStart && a.Date <= monthEnd)
                .Select(a => new { a.PersonId, a.Date, a.Portion })
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.PersonId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AbsenceDay(a.Date, a.Portion)).ToList());
        var records = (await db.RateRecords.AsNoTracking()
                .Where(r => ids.Contains(r.PersonId) && r.EffectiveFrom <= period.Start)
                .Select(r => new { r.Id, r.PersonId, r.EffectiveFrom, r.BilledMonthlyUsd, r.CommissionPerPeriodUsd, r.PayMonthlyAmount, r.PayCurrency })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PersonId)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.EffectiveFrom)!);

        var changes = new List<LineChange>();
        foreach (var personId in ids)
        {
            if (!people.TryGetValue(personId, out var person))
            {
                continue;
            }

            var spans = employment.GetValueOrDefault(personId) ?? [];
            var employed = EmploymentCalendar.EmployedWorkingDays(spans, period.Start, period.End) > 0;
            var line = run.Lines.SingleOrDefault(l => l.PersonId == personId);
            if (!employed && line is not { HasEntries: true })
            {
                if (line is not null)
                {
                    changes.Add(new LineChange(line.PersonCode, line.PersonName, "Line", "included", "removed (no employed working day)", false));
                    db.PayrollLines.Remove(line);
                    run.RemoveLine(line);
                }

                continue;
            }

            var record = records.GetValueOrDefault(personId);
            var terms = record is null ? null : new PayTerms(record.EffectiveFrom, record.BilledMonthlyUsd, record.CommissionPerPeriodUsd, record.PayMonthlyAmount, record.PayCurrency);
            var snapshot = new PayrollLineSnapshot(person.Code, person.FullName, person.Designation, person.Type, person.HireSource, person.BankName, person.Iban, record?.Id, terms);
            var isNew = line is null;
            line ??= run.AddLine(personId);
            var result = PayrollCalculator.Calculate(new PayrollInput(period, spans, absences.GetValueOrDefault(personId) ?? [], terms,
                person.HireSource, run.ExchangeRate, line.ExtraDays, line.AdjustmentInputs()));

            var before = isNew ? null : LineValues.From(line);
            line.Apply(snapshot, result, orphaned: !employed);
            var after = LineValues.From(line);
            if (before is null)
            {
                changes.Add(new LineChange(line.PersonCode, line.PersonName, "Line", "not included", "added", false));
            }
            else
            {
                changes.AddRange(before.Diff(after, line.PersonCode, line.PersonName));
            }
        }

        return changes;
    }

    // ===================== Helpers =====================

    private IQueryable<PayrollLineRow> LineRows(IQueryable<PayrollLine> lines) =>
        lines.Select(l => new PayrollLineRow(
            l.Id, l.PersonId, l.PersonCode, l.PersonName, l.Designation, l.PersonType, l.WorkingDays, l.EmployedWorkingDays, l.UnpaidDays,
            l.ExtraDays, l.PayableDays, l.PayMonthlyAmount, l.PayCurrency, l.PayPkr, l.PayUsd, l.AdjustmentsPkr, l.NetPayPkr, l.NetPayUsd,
            l.Issue, l.IsOrphaned, db.PayrollAdjustments.Count(a => a.LineId == l.Id)));

    private Task<PayrollRun?> LoadRunAsync(int runId, CancellationToken cancellationToken) =>
        db.PayrollRuns
            .Include(r => r.Lines).ThenInclude(l => l.Adjustments)
            .Include(r => r.Lines).ThenInclude(l => l.Absences)
            .AsSplitQuery()
            .SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);

    private async Task<(PayrollRun? Run, PayrollLine? Line, PayrollResult? Failure)> LoadDraftLineAsync(int runId, int lineId, CancellationToken cancellationToken)
    {
        var run = await LoadRunAsync(runId, cancellationToken);
        var line = run?.Lines.SingleOrDefault(l => l.Id == lineId);
        if (run is null || line is null)
        {
            return (null, null, new PayrollResult(PayrollResultStatus.NotFound));
        }

        return run.IsDraft
            ? (run, line, null)
            : (null, null, new PayrollResult(PayrollResultStatus.NotDraft, runId, [new PayrollError(string.Empty, NotDraftMessage)]));
    }

    private Task<DateOnly?> LatestFinalizedStartAsync(CancellationToken cancellationToken) =>
        db.PayrollRuns.AsNoTracking()
            .Where(r => r.Status == PayrollStatus.Finalized)
            .OrderByDescending(r => r.PeriodStart)
            .Select(r => (DateOnly?)r.PeriodStart)
            .FirstOrDefaultAsync(cancellationToken);

    private static PayrollResult? AdjustmentErrors(AdjustmentType? type, decimal? amount, PayCurrency? currency, string? note)
    {
        if (type is not { } t || !Enum.IsDefined(t))
        {
            return PayrollResult.Invalid("Type", "Choose Bonus, Reimbursement or Deduction.");
        }

        if (currency is not { } c || !Enum.IsDefined(c))
        {
            return PayrollResult.Invalid("Currency", "Choose USD or PKR.");
        }

        if (amount is not { } a)
        {
            return PayrollResult.Invalid("Amount", "Enter the amount.");
        }

        if (AdjustmentRules.AmountError(a, c) is { } error)
        {
            return PayrollResult.Invalid("Amount", error);
        }

        return note?.Trim() is { Length: > AdjustmentRules.NoteMaxLength } ? PayrollResult.Invalid("Note", "The note can be at most 300 characters.") : null;
    }

    private static string Describe(AdjustmentType type, decimal amount, PayCurrency currency) =>
        $"{type} {amount.ToString(currency == PayCurrency.PKR ? "0" : "0.00", CultureInfo.InvariantCulture)} {currency}";

    private static string RateText(decimal? rate) => rate?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "none";

    private async Task<PayrollResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new PayrollResult(PayrollResultStatus.Conflict, Errors: [new PayrollError(string.Empty, ConflictMessage)]);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: NoDeleteFinalizedErrorNumber or FrozenErrorNumber })
        {
            db.ChangeTracker.Clear();
            return new PayrollResult(PayrollResultStatus.NotDraft, Errors: [new PayrollError(string.Empty, NotDraftMessage)]);
        }
    }

    /// <summary>Every stored value of a line that a fresh calculation must reproduce, with the billing ones marked Admin-only.</summary>
    private sealed record LineValues(IReadOnlyList<(string Field, string Value, bool AdminOnly)> Values)
    {
        public static LineValues From(PayrollLine l)
        {
            static string D(decimal? v) => v?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—";
            return new LineValues(
            [
                ("Name", l.PersonName, false),
                ("Designation", l.Designation, false),
                ("Type", l.PersonType.ToString(), false),
                ("Bank details", $"{l.BankName} {l.Iban}", false),
                ("Hire source", l.HireSource?.ToString() ?? "none", true),
                ("Pay record", l.RateRecordId?.ToString(CultureInfo.InvariantCulture) ?? "none", false),
                ("Monthly pay", $"{D(l.PayMonthlyAmount)} {l.PayCurrency}", false),
                ("Monthly billing", D(l.BilledMonthlyUsd), true),
                ("Commission per period", D(l.CommissionPerPeriodUsd), true),
                ("Employed working days", l.EmployedWorkingDays.ToString(CultureInfo.InvariantCulture), false),
                ("Unpaid days", D(l.UnpaidDays), false),
                ("Payable days", D(l.PayableDays), false),
                ("Absences", string.Join(", ", l.Absences.OrderBy(a => a.Date).Select(a => $"{a.Date:yyyy-MM-dd} {a.Portion} {D(a.PaidDays)}/{D(a.UnpaidDays)}")), false),
                ("Salary part", D(l.SalaryPartUsd), true),
                ("Commission", D(l.CommissionUsd), true),
                ("Billed", D(l.BilledUsd), true),
                ("Pay (USD)", D(l.PayUsd), false),
                ("Pay (PKR)", D(l.PayPkr), false),
                ("Adjustments (PKR)", D(l.AdjustmentsPkr), false),
                ("Adjustments (USD)", D(l.AdjustmentsUsd), false),
                ("Net pay (PKR)", D(l.NetPayPkr), false),
                ("Net pay (USD)", D(l.NetPayUsd), false),
                ("Invoice", D(l.InvoiceUsd), true),
                ("Owner earning (USD)", D(l.OwnerEarningUsd), true),
                ("Owner earning (PKR)", D(l.OwnerEarningPkr), true),
                ("Issue", l.Issue?.ToString() ?? "none", false),
                ("Orphaned", l.IsOrphaned ? "yes" : "no", false),
            ]);
        }

        public IEnumerable<LineChange> Diff(LineValues after, string code, string name) =>
            Values.Zip(after.Values)
                .Where(p => p.First.Value != p.Second.Value)
                .Select(p => p.First.Field == "Bank details"
                    ? new LineChange(code, name, p.First.Field, "previous details", "updated details", false)
                    : new LineChange(code, name, p.First.Field, p.First.Value, p.Second.Value, p.First.AdminOnly));
    }
}
