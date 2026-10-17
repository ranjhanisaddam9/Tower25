using HR.Domain.Pay;

namespace HR.Domain.Reports;

/// <summary>
/// One rate record as the salary-changes report sees it (monthly USD). <see cref="BilledMonthlyUsd"/> and
/// <see cref="CommissionMonthlyUsd"/> are null for Managers: the caller never loads them.
/// </summary>
public sealed record RatePoint(DateOnly EffectiveFrom, decimal SalaryMonthlyUsd, decimal? BilledMonthlyUsd = null, decimal? CommissionMonthlyUsd = null);

/// <param name="SalaryChangePercent">Null when the old salary was 0.</param>
public sealed record SalaryChange(RatePoint Previous, RatePoint Current, decimal? SalaryChangePercent, bool SalaryChanged, bool BillingChanged);

/// <summary>Increments and decrements: rate records in a date range that changed the salary (or, for Admins, billing) from the record before.</summary>
public static class SalaryChanges
{
    public static IReadOnlyList<SalaryChange> Between(IEnumerable<RatePoint> history, DateOnly from, DateOnly to)
    {
        var ordered = history.OrderBy(r => r.EffectiveFrom).ToList();
        var result = new List<SalaryChange>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var (previous, current) = (ordered[i - 1], ordered[i]);
            if (current.EffectiveFrom < from || current.EffectiveFrom > to)
            {
                continue;
            }

            var salaryChanged = previous.SalaryMonthlyUsd != current.SalaryMonthlyUsd;
            var billingChanged = previous.BilledMonthlyUsd != current.BilledMonthlyUsd || previous.CommissionMonthlyUsd != current.CommissionMonthlyUsd;
            if (!salaryChanged && !billingChanged)
            {
                continue;
            }

            var percent = PayMath.PercentChange(previous.SalaryMonthlyUsd, current.SalaryMonthlyUsd);
            result.Add(new SalaryChange(previous, current, percent is { } p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null, salaryChanged, billingChanged));
        }

        return result;
    }
}
