using HR.Domain.Payroll;
using HR.Domain.Reports;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HR.Infrastructure.Reports;

/// <summary>One finalized payroll as the history report shows it to everyone: people and total net pay (PKR).</summary>
public sealed record PayrollHistoryRow(int RunId, DateOnly PeriodStart, DateOnly PeriodEnd, int People, decimal NetPayPkr, DateTimeOffset? FinalizedAt);

/// <summary>Admin only: the shared history row plus the run's invoice total and owner earning (frozen line values).</summary>
public sealed record AdminPayrollHistoryRow(PayrollHistoryRow Row, decimal InvoiceUsd, decimal OwnerEarningUsd, decimal OwnerEarningPkr);

/// <summary>One increment or decrement. Billing values inside <see cref="Change"/> are null for Managers (never loaded).</summary>
public sealed record SalaryChangeRow(int PersonId, string Code, string FullName, string Designation, SalaryChange Change);

/// <summary>
/// M9 reports. Every Manager-facing query selects pay columns only; the Admin variants are separate methods that add
/// billing. Payroll figures always come from finalized lines (frozen), never recalculated.
/// </summary>
public sealed class ReportService(AppDbContext db, IClock clock)
{
    public const int HistoryChartPeriods = 24;
    public const int TrendPeriods = 6;

    /// <summary>Finalized payrolls, newest first (optionally only the latest <paramref name="take"/>).</summary>
    public async Task<IReadOnlyList<PayrollHistoryRow>> PayrollHistoryAsync(int? take = null, CancellationToken cancellationToken = default)
    {
        var runs = db.PayrollRuns.AsNoTracking().Where(r => r.Status == PayrollStatus.Finalized).OrderByDescending(r => r.PeriodStart);
        var limited = take is { } n ? runs.Take(n) : runs;
        return await limited
            .Select(r => new PayrollHistoryRow(r.Id, r.PeriodStart, r.PeriodEnd,
                db.PayrollLines.Count(l => l.RunId == r.Id && !l.IsOrphaned),
                db.PayrollLines.Where(l => l.RunId == r.Id && !l.IsOrphaned).Sum(l => l.NetPayPkr ?? 0m),
                r.FinalizedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Admin only: the history with invoice and owner earning totals per run.</summary>
    public async Task<IReadOnlyList<AdminPayrollHistoryRow>> AdminPayrollHistoryAsync(int? take = null, CancellationToken cancellationToken = default)
    {
        var rows = await PayrollHistoryAsync(take, cancellationToken);
        var ids = rows.Select(r => r.RunId).ToList();
        var totals = (await db.PayrollLines.AsNoTracking()
                .Where(l => ids.Contains(l.RunId) && !l.IsOrphaned)
                .GroupBy(l => l.RunId)
                .Select(g => new { RunId = g.Key, Invoice = g.Sum(l => l.InvoiceUsd ?? 0m), Earning = g.Sum(l => l.OwnerEarningUsd ?? 0m), EarningPkr = g.Sum(l => l.OwnerEarningPkr ?? 0m) })
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.RunId);
        return rows.Select(r => totals.TryGetValue(r.RunId, out var t)
                ? new AdminPayrollHistoryRow(r, t.Invoice, t.Earning, t.EarningPkr)
                : new AdminPayrollHistoryRow(r, 0m, 0m, 0m))
            .ToList();
    }

    /// <summary>Pay changes (increments and decrements) that took effect in the range. Pay columns only.</summary>
    public async Task<IReadOnlyList<SalaryChangeRow>> SalaryChangesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var records = await db.RateRecords.AsNoTracking()
            .Select(r => new { r.PersonId, r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency })
            .ToListAsync(cancellationToken);
        var changes = records
            .GroupBy(r => r.PersonId)
            .SelectMany(g => SalaryChanges.Between(g.Select(r => new RatePoint(r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency)), from, to)
                .Where(c => c.PayChanged)
                .Select(c => (PersonId: g.Key, Change: c)))
            .ToList();
        return await WithPeopleAsync(changes, cancellationToken);
    }

    /// <summary>Admin only: pay and billing changes in the range.</summary>
    public async Task<IReadOnlyList<SalaryChangeRow>> AdminSalaryChangesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var records = await db.RateRecords.AsNoTracking()
            .Select(r => new { r.PersonId, r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency, r.BilledMonthlyUsd, r.CommissionPerPeriodUsd })
            .ToListAsync(cancellationToken);
        var changes = records
            .GroupBy(r => r.PersonId)
            .SelectMany(g => SalaryChanges.Between(
                    g.Select(r => new RatePoint(r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency, r.BilledMonthlyUsd, r.CommissionPerPeriodUsd)), from, to)
                .Select(c => (PersonId: g.Key, Change: c)))
            .ToList();
        return await WithPeopleAsync(changes, cancellationToken);
    }

    /// <summary>Headcount for the last 12 months, the current month last.</summary>
    public async Task<IReadOnlyList<HeadcountMonth>> HeadcountAsync(CancellationToken cancellationToken = default)
    {
        var types = await db.People.AsNoTracking().Select(p => new { p.Id, p.Type }).ToListAsync(cancellationToken);
        var spans = (await db.EmploymentPeriods.AsNoTracking().Select(e => new { e.PersonId, e.StartDate, e.EndDate }).ToListAsync(cancellationToken))
            .ToLookup(e => e.PersonId, e => new HR.Domain.People.EmploymentSpan(e.StartDate, e.EndDate));
        return HeadcountCalculator.LastMonths(types.Select(t => new HeadcountPerson(t.Type, spans[t.Id].ToList())), clock.Today);
    }

    private async Task<IReadOnlyList<SalaryChangeRow>> WithPeopleAsync(List<(int PersonId, SalaryChange Change)> changes, CancellationToken cancellationToken)
    {
        var ids = changes.Select(c => c.PersonId).Distinct().ToList();
        var people = await db.People.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Code, p.FullName, p.Designation })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        return changes
            .Select(c => new SalaryChangeRow(c.PersonId, people[c.PersonId].Code, people[c.PersonId].FullName, people[c.PersonId].Designation, c.Change))
            .OrderBy(c => c.Change.Current.EffectiveFrom)
            .ThenBy(c => c.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
