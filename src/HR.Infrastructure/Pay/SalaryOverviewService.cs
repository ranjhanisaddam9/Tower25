using HR.Domain.Pay;
using HR.Domain.Time;
using HR.Infrastructure.Data;
using HR.Infrastructure.Rates;
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

/// <summary>A salaries row as Managers see it: pay side only.</summary>
/// <param name="StartsOn">When the person has records but none in effect yet: the first EffectiveFrom.</param>
public sealed record SalaryRow(
    int PersonId,
    string Code,
    string FullName,
    string Designation,
    decimal? PayMonthlyAmount,
    PayCurrency? PayCurrency,
    DateOnly? Since,
    DateOnly? LastChangeDate,
    decimal? LastChangePercent,
    bool HasSetup,
    DateOnly? StartsOn);

/// <summary>Admin row: the shared row plus billing, commission, estimated earning and the review flag.</summary>
public sealed record AdminSalaryRow(SalaryRow Row, decimal? BilledMonthlyUsd, decimal? CommissionPerPeriodUsd, decimal? EarningPerFullPeriodUsd, bool NeedsReview);

/// <param name="EarningIsEstimate">True when any PKR-paid person was converted at today's rate.</param>
public sealed record AdminSalaryTotals(decimal BilledMonthlyUsd, decimal EarningPerFullPeriodUsd, bool EarningIsEstimate, int PeopleWithoutRate);

/// <summary>
/// The /salaries overview for active people. Rows are computed in memory from each person's records (the number of
/// people is small), so filters, sorting and totals use exactly the same rules as the person's pay tab.
/// </summary>
public sealed class SalaryOverviewService(AppDbContext db, IClock clock, IExchangeRateService rates)
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
                .Select(r => new { r.PersonId, r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PersonId)
            .ToDictionary(g => g.Key, g => g.Select(r => new PayPoint(r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency)).OrderBy(r => r.EffectiveFrom).ToList());

        var rows = people.Select(p => BuildRow(p, records.GetValueOrDefault(p.Id) ?? [])).ToList();
        var filter = query.Filter == SalaryFilter.NeedsReview ? SalaryFilter.All : query.Filter; // Admin-only filter ignored
        return Page(Sort(Filter(rows, filter, r => r), query, r => r), query);
    }

    public async Task<(PagedResult<AdminSalaryRow> Page, AdminSalaryTotals Totals)> ListForAdminAsync(SalaryQuery query, CancellationToken cancellationToken = default)
    {
        var people = await ActivePeopleAsync(query.Search, cancellationToken);
        var ids = people.Select(p => p.Id).ToList();
        var rate = (await rates.GetCurrentAsync(cancellationToken))?.UsdToPkr;
        var today = clock.Today;

        var all = (await db.RateRecords.AsNoTracking()
                .Where(r => ids.Contains(r.PersonId))
                .Select(r => new { r.PersonId, r.EffectiveFrom, r.BilledMonthlyUsd, r.CommissionPerPeriodUsd, r.PayMonthlyAmount, r.PayCurrency, r.NeedsBillingReview })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PersonId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.EffectiveFrom).ToList());

        var rows = new List<AdminSalaryRow>();
        foreach (var person in people)
        {
            var records = all.GetValueOrDefault(person.Id) ?? [];
            var row = BuildRow(person, records.Select(r => new PayPoint(r.EffectiveFrom, r.PayMonthlyAmount, r.PayCurrency)).ToList());
            var current = records.Where(r => r.EffectiveFrom <= today).MaxBy(r => r.EffectiveFrom);
            FullPeriodAmounts? amounts = current is null
                ? null
                : PayMath.FullPeriod(new PayTerms(current.EffectiveFrom, current.BilledMonthlyUsd, current.CommissionPerPeriodUsd, current.PayMonthlyAmount, current.PayCurrency), rate);
            rows.Add(new AdminSalaryRow(row, current?.BilledMonthlyUsd, current?.CommissionPerPeriodUsd, amounts?.EarningUsd, records.Any(r => r.NeedsBillingReview)));
        }

        var filtered = query.Filter == SalaryFilter.NeedsReview
            ? rows.Where(r => r.NeedsReview).ToList()
            : Filter(rows, query.Filter, r => r.Row);

        var totals = new AdminSalaryTotals(
            filtered.Sum(r => r.BilledMonthlyUsd ?? 0m),
            filtered.Sum(r => r.EarningPerFullPeriodUsd ?? 0m),
            filtered.Any(r => r.Row.PayCurrency == PayCurrency.PKR && r.EarningPerFullPeriodUsd is not null),
            filtered.Count(r => r.Row.HasSetup && r.Row.PayMonthlyAmount is not null && r.EarningPerFullPeriodUsd is null));

        return (Page(Sort(filtered, query, r => r.Row), query), totals);
    }

    private sealed record PersonInfo(int Id, string Code, int CodeNumber, string FullName, string Designation);

    private sealed record PayPoint(DateOnly EffectiveFrom, decimal Pay, PayCurrency Currency);

    private async Task<List<PersonInfo>> ActivePeopleAsync(string? search, CancellationToken cancellationToken)
    {
        var people = db.People.AsNoTracking().Where(p => p.IsActive);
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

        // The latest pay change (up or down) on or before today, compared with the record before it in the same currency.
        DateOnly? lastChange = null;
        decimal? lastPercent = null;
        var effective = records.Where(r => r.EffectiveFrom <= today).ToList();
        for (var i = effective.Count - 1; i > 0; i--)
        {
            if (effective[i].Pay != effective[i - 1].Pay || effective[i].Currency != effective[i - 1].Currency)
            {
                lastChange = effective[i].EffectiveFrom;
                lastPercent = effective[i].Currency == effective[i - 1].Currency ? PayMath.PercentChange(effective[i - 1].Pay, effective[i].Pay) : null;
                break;
            }
        }

        return new SalaryRow(person.Id, person.Code, person.FullName, person.Designation, current?.Pay, current?.Currency, current?.EffectiveFrom,
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
