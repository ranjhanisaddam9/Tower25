using HR.Domain.Payroll;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HR.Infrastructure.Payroll;

/// <summary>Everything the owner-income page shows for one selection.</summary>
public sealed record OwnerIncomeReport(
    IncomeView View,
    DateOnly Anchor,
    DateOnly Today,
    IncomeKpis Kpis,
    IncomeBreakdown Selection,
    IncomeBreakdown? DraftSelection,
    IReadOnlyList<(int Month, IncomeBreakdown Income)> YearByMonth,
    IReadOnlyList<(int Month, IncomeBreakdown Income)>? DraftYearByMonth,
    IReadOnlyList<IncomeContributor> Contributors,
    int FinalizedPeriodsInSelection,
    IReadOnlyList<DateOnly> DraftPeriods);

/// <summary>
/// Owner income (SPEC §8, M8), Admin only. Reads the frozen OwnerEarning values of finalized payroll lines and never
/// recalculates them from live data; drafts are read the same way but only when asked for, and kept separate.
/// </summary>
public sealed class OwnerIncomeService(AppDbContext db, IClock clock)
{
    public async Task<OwnerIncomeReport> GetAsync(IncomeView view, DateOnly? anchor, bool includeDraft, CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var at = anchor ?? today;
        var finalized = await LinesAsync(PayrollStatus.Finalized, cancellationToken);
        var drafts = includeDraft ? await LinesAsync(PayrollStatus.Draft, cancellationToken) : null;

        return new OwnerIncomeReport(
            view,
            at,
            today,
            OwnerIncomeAggregator.Kpis(finalized, today),
            OwnerIncomeAggregator.For(finalized, view, at),
            drafts is null ? null : OwnerIncomeAggregator.For(drafts, view, at),
            OwnerIncomeAggregator.ByMonth(finalized, at.Year),
            drafts is null ? null : OwnerIncomeAggregator.ByMonth(drafts, at.Year),
            OwnerIncomeAggregator.Contributors(finalized, view, at),
            finalized.Where(l => OwnerIncomeAggregator.InSelection(l.PeriodStart, view, at)).Select(l => l.PeriodStart).Distinct().Count(),
            drafts?.Where(l => OwnerIncomeAggregator.InSelection(l.PeriodStart, view, at)).Select(l => l.PeriodStart).Distinct().Order().ToList() ?? []);
    }

    private async Task<List<IncomeLine>> LinesAsync(PayrollStatus status, CancellationToken cancellationToken) =>
        (await (
                from l in db.PayrollLines.AsNoTracking()
                join r in db.PayrollRuns.AsNoTracking() on l.RunId equals r.Id
                where r.Status == status && !l.IsOrphaned
                select new { r.PeriodStart, l.PersonId, l.PersonName, l.HireSource, l.OwnerEarningUsd, l.OwnerEarningPkr })
            .ToListAsync(cancellationToken))
        .Select(x => new IncomeLine(x.PeriodStart, x.PersonId, x.PersonName, x.HireSource, x.OwnerEarningUsd ?? 0m, x.OwnerEarningPkr ?? 0m))
        .ToList();
}
