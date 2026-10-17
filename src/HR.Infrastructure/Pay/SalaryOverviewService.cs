using HR.Domain.People;
using HR.Domain.Pay;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HR.Infrastructure.Pay;

public enum SalaryFilter
{
    All,
    MissingSetup,
    ChangedRecently,

    /// <summary>Admin only.</summary>
    NeedsReview,
}

public enum SalarySort
{
    Name,
    Code,
    Since,
}

public sealed record SalaryQuery(string? Search, SalaryFilter Filter, SalarySort Sort, bool Descending, int Page, int PageSize = SalaryOverviewService.DefaultPageSize);

/// <summary>A salaries row as Managers see it: the salary (monthly USD) only.</summary>
/// <param name="StartsOn">When the person has records but none in effect yet: the first EffectiveFrom.</param>
public sealed record SalaryRow(
    int PersonId,
    string Code,
    string FullName,
    string Designation,
    decimal? SalaryMonthlyUsd,
    DateOnly? Since,
    DateOnly? LastChangeDate,
    decimal? LastChangePercent,
    bool HasSetup,
    DateOnly? StartsOn);

/// <summary>Admin row: the shared row plus billing, commission, estimated earning and the review flag.</summary>
public sealed record AdminSalaryRow(SalaryRow Row, decimal? BilledMonthlyUsd, decimal? CommissionMonthlyUsd, decimal? EarningPerFullPeriodUsd, bool NeedsReview);

public sealed record AdminSalaryTotals(decimal SalaryMonthlyUsd, decimal CommissionMonthlyUsd, decimal BilledMonthlyUsd, decimal EarningPerFullPeriodUsd);

/// <summary>
/// The /salaries overview for active people. Rows are computed in memory from each person's records (the number of
/// people is small), so filters, sorting and totals use exactly the same rules as the person's pay tab.
/// </summary>
public sealed class SalaryOverviewService(AppDbContext db, IClock clock)
{
    public const int DefaultPageSize = 20;
    public const int RecentDays = 90;

    public async Task<PagedResult<SalaryRow>> ListAsync(SalaryQuery query, CancellationToken cancellationToken = default)
    {
        var people = await ActivePeopleAsync(query.Search, cancellationToken);
        var ids = people.Select(p => p.Id).ToList();

        // Pay-side columns only.
        var records = (await db.RateRecords.AsNoTracking()
                .Where(r => ids.Contains(r.PersonId))
                .Select(r => new { r.PersonId, r.EffectiveFrom, r.SalaryMonthlyUsd })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PersonId)
            .ToDictionary(g => g.Key, g => g.Select(r => new PayPoint(r.EffectiveFrom, r.SalaryMonthlyUsd)).OrderBy(r => r.EffectiveFrom).ToList());

        var rows = people.Select(p => BuildRow(p, records.GetValueOrDefault(p.Id) ?? [])).ToList();
        var filter = query.Filter == SalaryFilter.NeedsReview ? SalaryFilter.All : query.Filter; // Admin-only filter ignored
        return Page(Sort(Filter(rows, filter, r => r), query, r => r), query);
    }

    public async Task<(PagedResult<AdminSalaryRow> Page, AdminSalaryTotals Totals)> ListForAdminAsync(SalaryQuery query, CancellationToken cancellationToken = default)
    {
        var people = await ActivePeopleAsync(query.Search, cancellationToken);
        var ids0 = people.Select(p => p.Id).ToList();
        var sources = await db.People.AsNoTracking().Where(p => ids0.Contains(p.Id)).Select(p => new { p.Id, p.HireSource }).ToDictionaryAsync(p => p.Id, p => p.HireSource, cancellationToken); // Admin only
        var ids = people.Select(p => p.Id).ToList();
        var today = clock.Today;

        var all = (await db.RateRecords.AsNoTracking()
                .Where(r => ids.Contains(r.PersonId))
                .Select(r => new { r.PersonId, r.EffectiveFrom, r.SalaryMonthlyUsd, r.CommissionMonthlyUsd, r.BilledMonthlyUsd, r.NeedsBillingReview })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PersonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.EffectiveFrom).ToList());

        var rows = new List<AdminSalaryRow>();
        foreach (var person in people)
        {
            var records = all.GetValueOrDefault(person.Id) ?? [];
            var row = BuildRow(person, records.Select(r => new PayPoint(r.EffectiveFrom, r.SalaryMonthlyUsd)).ToList());
            var current = records.Where(r => r.EffectiveFrom <= today).MaxBy(r => r.EffectiveFrom);
            FullPeriodAmounts? amounts = current is null
                ? null
                : PayMath.FullPeriod(new PayTerms(current.EffectiveFrom, current.SalaryMonthlyUsd, current.CommissionMonthlyUsd, current.BilledMonthlyUsd), null, sources.GetValueOrDefault(person.Id));
            rows.Add(new AdminSalaryRow(row, current?.BilledMonthlyUsd, current?.CommissionMonthlyUsd, amounts?.EarningUsd, records.Any(r => r.NeedsBillingReview)));
        }

        var filtered = query.Filter == SalaryFilter.NeedsReview
            ? rows.Where(r => r.NeedsReview).ToList()
            : Filter(rows, query.Filter, r => r.Row);

        var totals = new AdminSalaryTotals(
            filtered.Sum(r => r.Row.SalaryMonthlyUsd ?? 0m),
            filtered.Sum(r => r.CommissionMonthlyUsd ?? 0m),
            filtered.Sum(r => r.BilledMonthlyUsd ?? 0m),
            filtered.Sum(r => r.EarningPerFullPeriodUsd ?? 0m));

        return (Page(Sort(filtered, query, r => r.Row), query), totals);
    }

    private sealed record PersonInfo(int Id, string Code, int CodeNumber, string FullName, string Designation);

    private sealed record PayPoint(DateOnly EffectiveFrom, decimal Salary);

    private async Task<List<PersonInfo>> ActivePeopleAsync(string? search, CancellationToken cancellationToken)
    {
        var people = db.People.AsNoTracking().Where(PersonStatus.ActiveOn(clock.Today));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            people = people.Where(p => p.FullName.Contains(term) || p.Code.Contains(term) || p.Designation.Contains(term));
        }

        return await people
            .Select(p => new PersonInfo(p.Id, p.Code, p.CodeNumber, p.FullName, p.Designation))
            .ToListAsync(cancellationToken);
    }

    private SalaryRow BuildRow(PersonInfo person, List<PayPoint> records)
    {
        var today = clock.Today;
        var current = records.Where(r => r.EffectiveFrom <= today).MaxBy(r => r.EffectiveFrom);

        // The latest salary change (up or down) on or before today, compared with the record before it.
        DateOnly? lastChange = null;
        decimal? lastPercent = null;
        var effective = records.Where(r => r.EffectiveFrom <= today).ToList();
        for (var i = effective.Count - 1; i > 0; i--)
        {
            if (effective[i].Salary != effective[i - 1].Salary)
            {
                lastChange = effective[i].EffectiveFrom;
                lastPercent = PayMath.PercentChange(effective[i - 1].Salary, effective[i].Salary);
                break;
            }
        }

        return new SalaryRow(person.Id, person.Code, person.FullName, person.Designation, current?.Salary, current?.EffectiveFrom,
            lastChange, lastPercent, records.Count > 0, current is null && records.Count > 0 ? records[0].EffectiveFrom : null);
    }

    private List<T> Filter<T>(List<T> rows, SalaryFilter filter, Func<T, SalaryRow> row)
    {
        var since = clock.Today.AddDays(-RecentDays);
        return filter switch
        {
            SalaryFilter.MissingSetup => rows.Where(r => !row(r).HasSetup).ToList(),
            SalaryFilter.ChangedRecently => rows.Where(r => row(r).LastChangeDate is { } d && d >= since).ToList(),
            _ => rows,
        };
    }

    private static List<T> Sort<T>(List<T> rows, SalaryQuery query, Func<T, SalaryRow> row)
    {
        IOrderedEnumerable<T> ordered = query.Sort switch
        {
            SalarySort.Code => query.Descending ? rows.OrderByDescending(r => CodeNumber(row(r).Code)) : rows.OrderBy(r => CodeNumber(row(r).Code)),
            SalarySort.Since => query.Descending
                ? rows.OrderByDescending(r => row(r).Since ?? DateOnly.MinValue)
                : rows.OrderBy(r => row(r).Since ?? DateOnly.MaxValue),
            _ => query.Descending
                ? rows.OrderByDescending(r => row(r).FullName, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderBy(r => row(r).FullName, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.ThenBy(r => row(r).PersonId).ToList();
    }

    private static int CodeNumber(string code) =>
        int.TryParse(code.AsSpan(code.IndexOf('-') + 1), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;

    private static PagedResult<T> Page<T>(List<T> rows, SalaryQuery query)
    {
        var page = PagedResult<T>.ClampPage(query.Page, rows.Count, query.PageSize);
        return new PagedResult<T>(rows.Skip((page - 1) * query.PageSize).Take(query.PageSize).ToList(), page, query.PageSize, rows.Count);
    }
}
