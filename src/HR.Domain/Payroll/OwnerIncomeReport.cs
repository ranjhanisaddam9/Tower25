using HR.Domain.People;

namespace HR.Domain.Payroll;

/// <summary>One payroll line as owner income sees it: the frozen final owner earning and the line's hire source.</summary>
public sealed record IncomeLine(DateOnly PeriodStart, int PersonId, string PersonName, HireSource? Source, decimal EarningUsd, decimal EarningPkr);

/// <summary>SPEC §8 breakdown: own salary (Owner lines), commission (CompanyRecommended) and margin (BudgetHire).</summary>
public sealed record IncomeBreakdown(decimal SalaryUsd, decimal CommissionUsd, decimal MarginUsd, decimal SalaryPkr, decimal CommissionPkr, decimal MarginPkr)
{
    public static readonly IncomeBreakdown Zero = new(0m, 0m, 0m, 0m, 0m, 0m);

    public decimal TotalUsd => SalaryUsd + CommissionUsd + MarginUsd;

    public decimal TotalPkr => SalaryPkr + CommissionPkr + MarginPkr;

    public static IncomeBreakdown From(IEnumerable<IncomeLine> lines)
    {
        var list = lines.ToList();
        decimal Usd(HireSource s) => list.Where(l => l.Source == s).Sum(l => l.EarningUsd);
        decimal Pkr(HireSource s) => list.Where(l => l.Source == s).Sum(l => l.EarningPkr);
        return new IncomeBreakdown(
            Usd(HireSource.Owner), Usd(HireSource.CompanyRecommended), Usd(HireSource.BudgetHire),
            Pkr(HireSource.Owner), Pkr(HireSource.CompanyRecommended), Pkr(HireSource.BudgetHire));
    }
}

public enum IncomeView
{
    Period,
    Month,
    Year,
}

public sealed record IncomeContributor(int PersonId, string PersonName, HireSource? Source, int Periods, decimal EarningUsd, decimal EarningPkr, decimal SharePercent);

/// <summary>Headline figures: this month, year to date, the last 12 months and the average per finalized period in them.</summary>
public sealed record IncomeKpis(IncomeBreakdown ThisMonth, IncomeBreakdown YearToDate, IncomeBreakdown Last12Months, decimal AveragePerPeriodUsd, decimal AveragePerPeriodPkr, int PeriodsInLast12Months);

/// <summary>Aggregates owner income from frozen payroll lines (SPEC §8). Pure: the caller decides which lines (finalized, drafts).</summary>
public static class OwnerIncomeAggregator
{
    /// <summary>The first and last period starts of a selection anchored on <paramref name="anchor"/>.</summary>
    public static (DateOnly From, DateOnly To) Range(IncomeView view, DateOnly anchor) => view switch
    {
        IncomeView.Period => (PayPeriod.For(anchor).Start, PayPeriod.For(anchor).Start),
        IncomeView.Month => (new DateOnly(anchor.Year, anchor.Month, 1), new DateOnly(anchor.Year, anchor.Month, 16)),
        _ => (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year, 12, 16)),
    };

    public static bool InSelection(DateOnly periodStart, IncomeView view, DateOnly anchor)
    {
        var (from, to) = Range(view, anchor);
        return periodStart >= from && periodStart <= to;
    }

    /// <summary>The previous or next selection's anchor.</summary>
    public static DateOnly Step(IncomeView view, DateOnly anchor, int direction) => view switch
    {
        IncomeView.Period => direction < 0 ? PayPeriod.For(anchor).Previous().Start : PayPeriod.For(anchor).Next().Start,
        IncomeView.Month => new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(direction),
        _ => new DateOnly(anchor.Year + direction, 1, 1),
    };

    public static IncomeBreakdown For(IEnumerable<IncomeLine> lines, IncomeView view, DateOnly anchor) =>
        IncomeBreakdown.From(lines.Where(l => InSelection(l.PeriodStart, view, anchor)));

    /// <summary>Twelve entries (January–December) for a year.</summary>
    public static IReadOnlyList<(int Month, IncomeBreakdown Income)> ByMonth(IEnumerable<IncomeLine> lines, int year)
    {
        var list = lines.Where(l => l.PeriodStart.Year == year).ToList();
        return Enumerable.Range(1, 12).Select(m => (m, IncomeBreakdown.From(list.Where(l => l.PeriodStart.Month == m)))).ToList();
    }

    /// <summary>Who the income in the selection came from, largest first, with each person's share of the total.</summary>
    public static IReadOnlyList<IncomeContributor> Contributors(IEnumerable<IncomeLine> lines, IncomeView view, DateOnly anchor)
    {
        var selected = lines.Where(l => InSelection(l.PeriodStart, view, anchor)).ToList();
        var total = selected.Sum(l => l.EarningUsd);
        return selected
            .GroupBy(l => (l.PersonId, l.Source))
            .Select(g => new IncomeContributor(
                g.Key.PersonId,
                g.OrderByDescending(l => l.PeriodStart).First().PersonName,
                g.Key.Source,
                g.Select(l => l.PeriodStart).Distinct().Count(),
                g.Sum(l => l.EarningUsd),
                g.Sum(l => l.EarningPkr),
                total == 0m ? 0m : Math.Round(g.Sum(l => l.EarningUsd) / total * 100m, 1, MidpointRounding.AwayFromZero)))
            .OrderByDescending(c => c.EarningUsd)
            .ThenBy(c => c.PersonName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// KPIs for the calendar month of <paramref name="today"/>: this month (both periods), the year up to the end of this
    /// month, and the last 12 calendar months ending with this one (24 periods).
    /// </summary>
    public static IncomeKpis Kpis(IEnumerable<IncomeLine> lines, DateOnly today)
    {
        var list = lines.ToList();
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var nextMonth = thisMonth.AddMonths(1);
        var twelveMonthsFrom = thisMonth.AddMonths(-11);
        var last12 = list.Where(l => l.PeriodStart >= twelveMonthsFrom && l.PeriodStart < nextMonth).ToList();
        var periods = last12.Select(l => l.PeriodStart).Distinct().Count();
        var last12Income = IncomeBreakdown.From(last12);
        return new IncomeKpis(
            IncomeBreakdown.From(list.Where(l => l.PeriodStart >= thisMonth && l.PeriodStart < nextMonth)),
            IncomeBreakdown.From(list.Where(l => l.PeriodStart.Year == today.Year && l.PeriodStart < nextMonth)),
            last12Income,
            periods == 0 ? 0m : Money.RoundUsd(last12Income.TotalUsd / periods),
            periods == 0 ? 0m : Money.RoundPkr(last12Income.TotalPkr / periods),
            periods);
    }
}
