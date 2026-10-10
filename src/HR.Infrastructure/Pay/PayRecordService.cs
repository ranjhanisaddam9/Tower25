using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Data.Configurations;
using HR.Infrastructure.Rates;
using HR.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HR.Infrastructure.Pay;

// ---------- Admin read models (billing visible) ----------

public sealed record AdminPayRow(
    int Id,
    DateOnly EffectiveFrom,
    RateChangeType ChangeType,
    decimal PayMonthlyAmount,
    PayCurrency PayCurrency,
    decimal BilledMonthlyUsd,
    decimal CommissionPerPeriodUsd,
    decimal? PayChangePercent,
    string? Note,
    bool NeedsBillingReview,
    string? AddedBy);

public sealed record AdminPayCurrent(PayTerms Terms, FullPeriodAmounts FullPeriod);

/// <param name="Source">The person's hire source (null: pay setup can't start yet).</param>
public sealed record AdminPayTab(
    HireSource? Source,
    IReadOnlyList<AdminPayRow> Rows,
    AdminPayCurrent? Current,
    decimal? UsdToPkr,
    DateOnly EarliestEffectiveFrom,
    DateOnly DefaultEffectiveFrom,
    decimal DefaultCommission);

public sealed record AdminPayRecord(int Id, int PersonId, PayTerms Terms, RateChangeType ChangeType, string? Note, bool NeedsBillingReview, byte[] RowVersion);

// ---------- Manager read models (pay side only; never billed, commission, review or hire source) ----------

/// <param name="ChangeLabel">Initial, Increment, Decrement, Update or Correction ("BillingChange" is shown as "Update").</param>
public sealed record ManagerPayRow(
    int Id,
    DateOnly EffectiveFrom,
    string ChangeLabel,
    decimal PayMonthlyAmount,
    PayCurrency PayCurrency,
    decimal? PayChangePercent,
    string? Note,
    string? AddedBy,
    bool CreatedByMe);

public sealed record ManagerPayCurrent(decimal PayMonthlyAmount, PayCurrency PayCurrency, DateOnly EffectiveFrom, decimal PayPerFullPeriod);

/// <param name="HasSetup">False when there is no hire source or no record yet: the Manager sees the neutral message.</param>
public sealed record ManagerPayTab(
    bool HasSetup,
    IReadOnlyList<ManagerPayRow> Rows,
    ManagerPayCurrent? Current,
    decimal? UsdToPkr,
    DateOnly EarliestEffectiveFrom,
    DateOnly DefaultEffectiveFrom);

public sealed record ManagerPayRecord(int Id, int PersonId, DateOnly EffectiveFrom, decimal PayMonthlyAmount, PayCurrency PayCurrency, string? Note, byte[] RowVersion);

// ---------- Commands ----------

public enum PayResultStatus
{
    Success,
    NotFound,
    Invalid,
    Forbidden,
    Locked,
    NeedsLossConfirmation,
    Conflict,
    NoSetup,
}

public sealed record PayResult(PayResultStatus Status, int? Id = null, IReadOnlyList<PayError>? Errors = null, decimal? LossPayUsd = null)
{
    public bool Succeeded => Status == PayResultStatus.Success;

    public static PayResult Invalid(string field, string message) => new(PayResultStatus.Invalid, Errors: [new PayError(field, message)]);
}

/// <summary>
/// Rate records (SPEC §2): Admin pay setup and corrections, Manager increments, the salaries overview and dashboard counts.
/// Manager-facing methods project pay fields only, so billed amounts, commission and review flags are never loaded for them.
/// </summary>
public sealed class PayRecordService(
    AppDbContext db,
    IClock clock,
    IExchangeRateService rates,
    IPayrollLock payrollLock,
    ILoggerFactory loggerFactory)
{
    public const string DuplicateMessage = "This person already has a pay record effective from that date. Edit that record instead.";
    public const string LockedMessage = "That pay period is part of a finalized payroll, so its pay records can't be changed.";
    public const string HireSourceLockedMessage = "Delete this person's pay records before changing the hire source.";
    public const string NoRecordYetMessage = PayRules.NoHireSourceMessage;

    private readonly ILogger _log = loggerFactory.CreateLogger(SecurityLog.Category);

    // ===================== Admin =====================

    public async Task<AdminPayTab?> GetAdminTabAsync(int personId, CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        if (person is null)
        {
            return null;
        }

        var rows = await (
                from r in db.RateRecords.AsNoTracking()
                where r.PersonId == personId
                join u in db.Users on r.CreatedByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby r.EffectiveFrom
                select new
                {
                    r.Id, r.EffectiveFrom, r.ChangeType, r.PayMonthlyAmount, r.PayCurrency, r.BilledMonthlyUsd,
                    r.CommissionPerPeriodUsd, r.Note, r.NeedsBillingReview, AddedBy = u == null ? null : u.FullName,
                })
            .ToListAsync(cancellationToken);

        var rate = (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr;
        var list = rows
            .Select((r, i) => new AdminPayRow(r.Id, r.EffectiveFrom, r.ChangeType, r.PayMonthlyAmount, r.PayCurrency, r.BilledMonthlyUsd,
                r.CommissionPerPeriodUsd,
                i > 0 && rows[i - 1].PayCurrency == r.PayCurrency ? PayMath.PercentChange(rows[i - 1].PayMonthlyAmount, r.PayMonthlyAmount) : null,
                r.Note, r.NeedsBillingReview, r.AddedBy))
            .Reverse()
            .ToList();

        var currentRow = rows.Where(r => r.EffectiveFrom <= clock.Today).MaxBy(r => r.EffectiveFrom);
        AdminPayCurrent? current = null;
        if (currentRow is not null)
        {
            var terms = new PayTerms(currentRow.EffectiveFrom, currentRow.BilledMonthlyUsd, currentRow.CommissionPerPeriodUsd, currentRow.PayMonthlyAmount, currentRow.PayCurrency);
            current = new AdminPayCurrent(terms, PayMath.FullPeriod(terms, rate));
        }

        var latest = rows.MaxBy(r => r.EffectiveFrom);
        return new AdminPayTab(
            person.Source,
            list,
            current,
            rate,
            person.Earliest,
            DefaultEffectiveFrom(person, rows.Count == 0),
            latest?.CommissionPerPeriodUsd is { } c && person.Source == HireSource.CompanyRecommended ? c : PayRules.DefaultCommission);
    }

    public Task<AdminPayRecord?> GetAdminRecordAsync(int personId, int recordId, CancellationToken cancellationToken = default) =>
        db.RateRecords.AsNoTracking()
            .Where(r => r.Id == recordId && r.PersonId == personId)
            .Select(r => new AdminPayRecord(r.Id, r.PersonId,
                new PayTerms(r.EffectiveFrom, r.BilledMonthlyUsd, r.CommissionPerPeriodUsd, r.PayMonthlyAmount, r.PayCurrency),
                r.ChangeType, r.Note, r.NeedsBillingReview, r.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<PayResult> CreateAdminAsync(int personId, AdminPayInput input, string? note, bool confirmLoss, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        if (person is null)
        {
            return new PayResult(PayResultStatus.NotFound);
        }

        if (person.Source is not { } source)
        {
            return new PayResult(PayResultStatus.NoSetup, Errors: [new PayError(string.Empty, "Set this person's hire source before setting up pay.")]);
        }

        var terms = PayRules.DeriveAdmin(source, input, person.Earliest, out var errors);
        if (terms is null)
        {
            return new PayResult(PayResultStatus.Invalid, Errors: errors);
        }

        if (await IsLockedAsync(terms.EffectiveFrom, cancellationToken))
        {
            return new PayResult(PayResultStatus.Locked, Errors: [new PayError(PayFields.EffectiveFrom, LockedMessage)]);
        }

        if (await DuplicateAsync(personId, terms.EffectiveFrom, exceptId: null, cancellationToken))
        {
            return PayResult.Invalid(PayFields.EffectiveFrom, DuplicateMessage);
        }

        var rate = (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr;
        if (LossNeedsConfirmation(source, terms, rate, confirmLoss) is { } loss)
        {
            return loss;
        }

        var record = RateRecord.Create(personId, terms, note, needsBillingReview: false, actorId, clock.UtcNow);
        db.RateRecords.Add(record);
        await RederiveChangeTypesAsync(personId, record, markCorrection: false, rate, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.RateRecordCreated(_log, actorId, record.Id, personId, record.ChangeType.ToString(), record.EffectiveFrom,
            record.BilledMonthlyUsd, record.CommissionPerPeriodUsd, record.PayMonthlyAmount, record.PayCurrency.ToString());
        return new PayResult(PayResultStatus.Success, record.Id);
    }

    public async Task<PayResult> UpdateAdminAsync(
        int personId,
        int recordId,
        AdminPayInput input,
        string? note,
        bool markCorrection,
        bool confirmLoss,
        byte[] rowVersion,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        var record = await db.RateRecords.SingleOrDefaultAsync(r => r.Id == recordId && r.PersonId == personId, cancellationToken);
        if (person is null || record is null)
        {
            return new PayResult(PayResultStatus.NotFound);
        }

        if (person.Source is not { } source)
        {
            return new PayResult(PayResultStatus.NoSetup, Errors: [new PayError(string.Empty, "Set this person's hire source first.")]);
        }

        if (!record.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new PayResult(PayResultStatus.Conflict, recordId);
        }

        var terms = PayRules.DeriveAdmin(source, input, person.Earliest, out var errors);
        if (terms is null)
        {
            return new PayResult(PayResultStatus.Invalid, Errors: errors);
        }

        if (await IsLockedAsync(record.EffectiveFrom, cancellationToken) || await IsLockedAsync(terms.EffectiveFrom, cancellationToken))
        {
            return new PayResult(PayResultStatus.Locked, Errors: [new PayError(string.Empty, LockedMessage)]);
        }

        if (await DuplicateAsync(personId, terms.EffectiveFrom, exceptId: recordId, cancellationToken))
        {
            return PayResult.Invalid(PayFields.EffectiveFrom, DuplicateMessage);
        }

        var rate = (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr;
        if (LossNeedsConfirmation(source, terms, rate, confirmLoss) is { } loss)
        {
            return loss;
        }

        var old = record.Terms;
        db.Entry(record).Property(r => r.RowVersion).OriginalValue = rowVersion;
        // An Admin edit settles any pending billing review for this record.
        record.Update(terms, note, needsBillingReview: false, actorId, clock.UtcNow);
        await RederiveChangeTypesAsync(personId, record, markCorrection, rate, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.RateRecordEdited(_log, actorId, record.Id, personId, Describe(old), Describe(record.Terms), record.ChangeType.ToString());
        return new PayResult(PayResultStatus.Success, record.Id);
    }

    public async Task<PayResult> MarkReviewedAsync(int personId, int recordId, string actorId, CancellationToken cancellationToken = default)
    {
        var record = await db.RateRecords.SingleOrDefaultAsync(r => r.Id == recordId && r.PersonId == personId, cancellationToken);
        if (record is null)
        {
            return new PayResult(PayResultStatus.NotFound);
        }

        record.MarkReviewed(actorId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        SecurityLog.RateRecordReviewed(_log, actorId, record.Id, personId);
        return new PayResult(PayResultStatus.Success, record.Id);
    }

    // ===================== Manager (and Admin) increments =====================

    public async Task<ManagerPayTab?> GetManagerTabAsync(int personId, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        if (person is null)
        {
            return null;
        }

        // Pay-side columns only.
        var rows = await (
                from r in db.RateRecords.AsNoTracking()
                where r.PersonId == personId
                join u in db.Users on r.CreatedByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                orderby r.EffectiveFrom
                select new { r.Id, r.EffectiveFrom, r.ChangeType, r.PayMonthlyAmount, r.PayCurrency, r.Note, r.CreatedByUserId, AddedBy = u == null ? null : u.FullName })
            .ToListAsync(cancellationToken);

        var list = rows
            .Select((r, i) => new ManagerPayRow(r.Id, r.EffectiveFrom, ManagerLabel(r.ChangeType), r.PayMonthlyAmount, r.PayCurrency,
                i > 0 && rows[i - 1].PayCurrency == r.PayCurrency ? PayMath.PercentChange(rows[i - 1].PayMonthlyAmount, r.PayMonthlyAmount) : null,
                r.Note, r.AddedBy, string.Equals(r.CreatedByUserId, actorId, StringComparison.Ordinal)))
            .Reverse()
            .ToList();

        var currentRow = rows.Where(r => r.EffectiveFrom <= clock.Today).MaxBy(r => r.EffectiveFrom);
        var current = currentRow is null
            ? null
            : new ManagerPayCurrent(currentRow.PayMonthlyAmount, currentRow.PayCurrency, currentRow.EffectiveFrom,
                PayMath.PayPerFullPeriod(currentRow.PayMonthlyAmount, currentRow.PayCurrency));

        return new ManagerPayTab(
            person.Source is not null && rows.Count > 0,
            list,
            current,
            (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr,
            person.Earliest,
            DefaultEffectiveFrom(person, firstRecord: false));
    }

    /// <summary>A record the actor created, pay side only (for the Manager's edit form). Null when missing or not theirs.</summary>
    public Task<ManagerPayRecord?> GetOwnRecordAsync(int personId, int recordId, string actorId, CancellationToken cancellationToken = default) =>
        db.RateRecords.AsNoTracking()
            .Where(r => r.Id == recordId && r.PersonId == personId && r.CreatedByUserId == actorId)
            .Select(r => new ManagerPayRecord(r.Id, r.PersonId, r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency, r.Note, r.RowVersion))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>The pay currency shown read-only on the increment form (from the latest record), or null when there is no setup.</summary>
    public async Task<PayCurrency?> GetIncrementCurrencyAsync(int personId, CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        if (person?.Source is null)
        {
            return null;
        }

        return await db.RateRecords.AsNoTracking()
            .Where(r => r.PersonId == personId)
            .OrderByDescending(r => r.EffectiveFrom)
            .Select(r => (PayCurrency?)r.PayCurrency)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// A Manager's increment: date, pay and note from the form; everything else derived from the record it follows
    /// (SPEC §2). BudgetHire increments are flagged for the Admin's billing review.
    /// </summary>
    public async Task<PayResult> CreateIncrementAsync(int personId, DateOnly? effectiveFrom, decimal? pay, string? note, string actorId, CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        if (person is null)
        {
            return new PayResult(PayResultStatus.NotFound);
        }

        var records = await db.RateRecords.Where(r => r.PersonId == personId).OrderBy(r => r.EffectiveFrom).ToListAsync(cancellationToken);
        if (person.Source is not { } source || records.Count == 0)
        {
            return new PayResult(PayResultStatus.NoSetup, Errors: [new PayError(string.Empty, NoRecordYetMessage)]);
        }

        var basis = (effectiveFrom is { } date ? records.Where(r => r.EffectiveFrom < date).MaxBy(r => r.EffectiveFrom) : null) ?? records[0];
        var terms = PayRules.DeriveIncrement(source, basis.Terms, effectiveFrom, pay, person.Earliest, out var errors);
        if (terms is null)
        {
            return new PayResult(PayResultStatus.Invalid, Errors: errors);
        }

        if (await IsLockedAsync(terms.EffectiveFrom, cancellationToken))
        {
            return new PayResult(PayResultStatus.Locked, Errors: [new PayError(PayFields.EffectiveFrom, LockedMessage)]);
        }

        if (records.Any(r => r.EffectiveFrom == terms.EffectiveFrom))
        {
            return PayResult.Invalid(PayFields.EffectiveFrom, DuplicateMessage);
        }

        var rate = (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr;
        var record = RateRecord.Create(personId, terms, note, needsBillingReview: source == HireSource.BudgetHire, actorId, clock.UtcNow);
        db.RateRecords.Add(record);
        await RederiveChangeTypesAsync(personId, record, markCorrection: false, rate, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.RateRecordCreated(_log, actorId, record.Id, personId, record.ChangeType.ToString(), record.EffectiveFrom,
            record.BilledMonthlyUsd, record.CommissionPerPeriodUsd, record.PayMonthlyAmount, record.PayCurrency.ToString());
        return new PayResult(PayResultStatus.Success, record.Id);
    }

    /// <summary>Edits a record the actor created: pay, date and note only; the same derivation rules as a new increment.</summary>
    public async Task<PayResult> UpdateIncrementAsync(
        int personId,
        int recordId,
        DateOnly? effectiveFrom,
        decimal? pay,
        string? note,
        byte[] rowVersion,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        var person = await PersonPayInfoAsync(personId, cancellationToken);
        var record = await db.RateRecords.SingleOrDefaultAsync(r => r.Id == recordId && r.PersonId == personId, cancellationToken);
        if (person is null || record is null)
        {
            return new PayResult(PayResultStatus.NotFound);
        }

        if (!string.Equals(record.CreatedByUserId, actorId, StringComparison.Ordinal))
        {
            return new PayResult(PayResultStatus.Forbidden);
        }

        if (person.Source is not { } source)
        {
            return new PayResult(PayResultStatus.NoSetup, Errors: [new PayError(string.Empty, NoRecordYetMessage)]);
        }

        if (!record.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return new PayResult(PayResultStatus.Conflict, recordId);
        }

        var terms = PayRules.DeriveIncrement(source, record.Terms, effectiveFrom, pay, person.Earliest, out var errors);
        if (terms is null)
        {
            return new PayResult(PayResultStatus.Invalid, Errors: errors);
        }

        if (await IsLockedAsync(record.EffectiveFrom, cancellationToken) || await IsLockedAsync(terms.EffectiveFrom, cancellationToken))
        {
            return new PayResult(PayResultStatus.Locked, Errors: [new PayError(string.Empty, LockedMessage)]);
        }

        if (await DuplicateAsync(personId, terms.EffectiveFrom, exceptId: recordId, cancellationToken))
        {
            return PayResult.Invalid(PayFields.EffectiveFrom, DuplicateMessage);
        }

        var old = record.Terms;
        db.Entry(record).Property(r => r.RowVersion).OriginalValue = rowVersion;
        record.Update(terms, note, needsBillingReview: record.NeedsBillingReview || source == HireSource.BudgetHire, actorId, clock.UtcNow);
        await RederiveChangeTypesAsync(personId, record, markCorrection: false, (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr, cancellationToken);
        if (await SaveAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        SecurityLog.RateRecordEdited(_log, actorId, record.Id, personId, Describe(old), Describe(record.Terms), record.ChangeType.ToString());
        return new PayResult(PayResultStatus.Success, record.Id);
    }

    /// <summary>Admins may delete any record; Managers only records they created. Refused inside a locked period.</summary>
    public async Task<PayResult> DeleteAsync(int personId, int recordId, string actorId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        var record = await db.RateRecords.SingleOrDefaultAsync(r => r.Id == recordId && r.PersonId == personId, cancellationToken);
        if (record is null)
        {
            return new PayResult(PayResultStatus.NotFound);
        }

        if (!isAdmin && !string.Equals(record.CreatedByUserId, actorId, StringComparison.Ordinal))
        {
            return new PayResult(PayResultStatus.Forbidden);
        }

        if (await IsLockedAsync(record.EffectiveFrom, cancellationToken))
        {
            return new PayResult(PayResultStatus.Locked, Errors: [new PayError(string.Empty, LockedMessage)]);
        }

        var old = record.Terms;
        db.RateRecords.Remove(record);
        await RederiveChangeTypesAsync(personId, null, markCorrection: false, (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        SecurityLog.RateRecordDeleted(_log, actorId, recordId, personId, Describe(old));
        return new PayResult(PayResultStatus.Success, recordId);
    }

    // ===================== Overview and counts =====================

    public Task<bool> HasRecordsAsync(int personId, CancellationToken cancellationToken = default) =>
        db.RateRecords.AnyAsync(r => r.PersonId == personId, cancellationToken);

    /// <summary>Active people without any pay record (both roles).</summary>
    public Task<int> CountActiveWithoutPaySetupAsync(CancellationToken cancellationToken = default) =>
        db.People.AsNoTracking().CountAsync(p => p.IsActive && !db.RateRecords.Any(r => r.PersonId == p.Id), cancellationToken);

    /// <summary>Admin only: records flagged for billing review (active people).</summary>
    public Task<int> CountPendingReviewsAsync(CancellationToken cancellationToken = default) =>
        db.RateRecords.AsNoTracking().CountAsync(r => r.NeedsBillingReview && db.People.Any(p => p.Id == r.PersonId && p.IsActive), cancellationToken);

    // ===================== Helpers =====================

    private sealed record PersonPayInfo(int Id, HireSource? Source, DateOnly Earliest, DateOnly JoiningDate);

    private async Task<PersonPayInfo?> PersonPayInfoAsync(int personId, CancellationToken cancellationToken)
    {
        var person = await db.People.AsNoTracking()
            .Where(p => p.Id == personId)
            .Select(p => new
            {
                p.Id,
                p.HireSource,
                p.JoiningDate,
                FirstStart = db.EmploymentPeriods.Where(e => e.PersonId == p.Id).Min(e => (DateOnly?)e.StartDate),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return person is null
            ? null
            : new PersonPayInfo(person.Id, person.HireSource, PayRules.EarliestEffectiveFrom(person.FirstStart ?? person.JoiningDate), person.JoiningDate);
    }

    private DateOnly DefaultEffectiveFrom(PersonPayInfo person, bool firstRecord)
    {
        var date = firstRecord ? PayPeriod.For(person.JoiningDate).Start : PayPeriod.For(clock.Today).Start;
        return date < person.Earliest ? person.Earliest : date;
    }

    private Task<bool> DuplicateAsync(int personId, DateOnly effectiveFrom, int? exceptId, CancellationToken cancellationToken) =>
        db.RateRecords.AnyAsync(r => r.PersonId == personId && r.EffectiveFrom == effectiveFrom && r.Id != exceptId, cancellationToken);

    private Task<bool> IsLockedAsync(DateOnly effectiveFrom, CancellationToken cancellationToken) =>
        payrollLock.IsLockedAsync(PayPeriod.For(effectiveFrom).Start, cancellationToken);

    private static PayResult? LossNeedsConfirmation(HireSource source, PayTerms terms, decimal? rate, bool confirmed)
    {
        if (source != HireSource.BudgetHire || confirmed || PayRules.LosesMoney(terms, rate) != true)
        {
            return null;
        }

        var payUsd = terms.PayCurrency == PayCurrency.USD ? terms.PayMonthlyAmount : Money.RoundUsd(terms.PayMonthlyAmount / rate!.Value);
        return new PayResult(PayResultStatus.NeedsLossConfirmation, LossPayUsd: payUsd);
    }

    /// <summary>
    /// Re-derives ChangeType for every record of the person in date order (they depend on the record before them).
    /// Records already marked Correction keep it, except <paramref name="changed"/> unless <paramref name="markCorrection"/>.
    /// </summary>
    private async Task RederiveChangeTypesAsync(int personId, RateRecord? changed, bool markCorrection, decimal? rate, CancellationToken cancellationToken)
    {
        var tracked = await db.RateRecords.Where(r => r.PersonId == personId).ToListAsync(cancellationToken);
        var all = tracked
            .Where(r => db.Entry(r).State != EntityState.Deleted)
            .Concat(changed is not null && !tracked.Contains(changed) ? [changed] : [])
            .OrderBy(r => r.EffectiveFrom)
            .ToList();

        PayTerms? previous = null;
        foreach (var record in all)
        {
            if (record == changed)
            {
                record.SetChangeType(markCorrection ? RateChangeType.Correction : PayRules.DeriveChangeType(previous, record.Terms, rate));
            }
            else if (record.ChangeType != RateChangeType.Correction)
            {
                record.SetChangeType(PayRules.DeriveChangeType(previous, record.Terms, rate));
            }

            previous = record.Terms;
        }

        // The first record is always the Initial one.
        if (all.Count > 0 && !(all[0] == changed && markCorrection))
        {
            all[0].SetChangeType(RateChangeType.Initial);
        }
    }

    private async Task<PayResult?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new PayResult(PayResultStatus.Conflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql
            && sql.Message.Contains(RateRecordConfiguration.EffectiveFromIndex, StringComparison.Ordinal))
        {
            db.ChangeTracker.Clear();
            return PayResult.Invalid(PayFields.EffectiveFrom, DuplicateMessage);
        }
    }

    private static string ManagerLabel(RateChangeType type) => type == RateChangeType.BillingChange ? "Update" : type.ToString();

    private static string Describe(PayTerms t) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{t.EffectiveFrom:yyyy-MM-dd} billed {t.BilledMonthlyUsd:0.00} USD, commission {t.CommissionPerPeriodUsd:0.00} USD, pay {t.PayMonthlyAmount:0.00} {t.PayCurrency}");
}
