using HR.Domain.Pay;

namespace HR.Domain.Reports;

/// <summary>
/// One rate record as the salary-changes report sees it. <see cref="BilledMonthlyUsd"/> and <see cref="CommissionPerPeriodUsd"/>
/// are null for Managers: the caller never loads them.
/// </summary>
public sealed record RatePoint(DateOnly EffectiveFrom, decimal PayMonthlyAmount, PayCurrency PayCurrency, decimal? BilledMonthlyUsd = null, decimal? CommissionPerPeriodUsd = null);

/// <param name="PayChangePercent">Null when the currency changed (not comparable) or the old pay was 0.</param>
public sealed record SalaryChange(RatePoint Previous, RatePoint Current, decimal? PayChangePercent, bool PayChanged, bool BillingChanged);

/// <summary>Increments and decrements: rate records in a date range that changed pay (or, for Admins, billing) from the record before.</summary>
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

            var payChanged = previous.PayMonthlyAmount != current.PayMonthlyAmount || previous.PayCurrency != current.PayCurrency;
            var billingChanged = previous.BilledMonthlyUsd != current.BilledMonthlyUsd || previous.CommissionPerPeriodUsd != current.CommissionPerPeriodUsd;
            if (!payChanged && !billingChanged)
            {
                continue;
            }

            var percent = previous.PayCurrency == current.PayCurrency ? PayMath.PercentChange(previous.PayMonthlyAmount, current.PayMonthlyAmount) : null;
            result.Add(new SalaryChange(previous, current, percent is { } p ? Math.Round(p, 2, MidpointRounding.AwayFromZero) : null, payChanged, billingChanged));
        }

        return result;
    }
}
