using HR.Domain.Payroll;

namespace HR.Domain.Pay;

/// <summary>Amounts for one FULL pay period (f = 1), rounded exactly as SPEC §5 does.</summary>
/// <param name="PayPkr">The salary in PKR at the given rate; null when there is no rate (an estimate before payroll).</param>
/// <param name="EarningUsd">BilledUsd − SalaryUsd (the commission); for an Owner the whole BilledUsd.</param>
public sealed record FullPeriodAmounts(
    decimal SalaryUsd,
    decimal CommissionUsd,
    decimal BilledUsd,
    decimal? PayPkr,
    decimal EarningUsd);

public static class PayMath
{
    /// <summary>
    /// SPEC §5 with f = 1: SalaryUsd = round2(salary/2), CommissionUsd = round2(commission/2), BilledUsd = their sum,
    /// PayPkr = round0(SalaryUsd × rate).
    /// </summary>
    public static FullPeriodAmounts FullPeriod(PayTerms terms, decimal? usdToPkr, HR.Domain.People.HireSource? source = null)
    {
        var salary = HalfMonth(terms.SalaryMonthlyUsd);
        var commission = HalfMonth(terms.CommissionMonthlyUsd);
        var billed = salary + commission;
        decimal? payPkr = usdToPkr is { } rate && rate > 0 ? Money.RoundPkr(salary * rate) : null;

        // SPEC §5: the Owner's whole billed amount is his income.
        var earning = source == HR.Domain.People.HireSource.Owner ? billed : billed - salary;
        return new FullPeriodAmounts(salary, commission, billed, payPkr, earning);
    }

    /// <summary>A monthly USD amount for one full pay period: round2(monthly / 2).</summary>
    public static decimal HalfMonth(decimal monthlyUsd) => Money.RoundUsd(monthlyUsd / 2);

    /// <summary>A USD amount in PKR at a rate (an estimate for display): round0(amount × rate).</summary>
    public static decimal ToPkr(decimal usd, decimal usdToPkr) => Money.RoundPkr(usd * usdToPkr);

    /// <summary>Percentage change between two amounts (null if not comparable).</summary>
    public static decimal? PercentChange(decimal previous, decimal current) =>
        previous == 0 ? null : (current - previous) / previous * 100m;
}

/// <summary>Which rate record applies on a date: the latest EffectiveFrom on or before it. Payroll uses InEffectOn(period.Start).</summary>
public static class RateRecords
{
    public static RateRecord? InEffectOn(IEnumerable<RateRecord> personRecords, DateOnly date) =>
        personRecords.Where(r => r.EffectiveFrom <= date).MaxBy(r => r.EffectiveFrom);

    public static PayTerms? InEffectOn(IEnumerable<PayTerms> personTerms, DateOnly date) =>
        personTerms.Where(t => t.EffectiveFrom <= date).MaxBy(t => t.EffectiveFrom);
}

/// <summary>A payroll period is locked ⇔ its payroll run is Finalized (SPEC §6).</summary>
public interface IPayrollLock
{
    Task<bool> IsLockedAsync(DateOnly periodStart, CancellationToken cancellationToken = default);

    /// <summary>The start of the latest locked period, or null when nothing is locked.</summary>
    Task<DateOnly?> LatestLockedPeriodStartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the locked period's finalized payroll includes this person (they have a line in it). Changing their days
    /// there would alter what was paid. The default assumes yes whenever the period is locked.
    /// </summary>
    Task<bool> IsLockedForPersonAsync(int personId, DateOnly periodStart, CancellationToken cancellationToken = default) =>
        IsLockedAsync(periodStart, cancellationToken);
}

public static class PayrollLockExtensions
{
    /// <summary>
    /// The start of the first locked period that overlaps <paramref name="from"/>–<paramref name="to"/> (inclusive;
    /// null = no end), or null. Only periods up to the latest locked one are checked.
    /// </summary>
    public static async Task<DateOnly?> FirstLockedAsync(this IPayrollLock payrollLock, DateOnly from, DateOnly? to, CancellationToken cancellationToken = default)
    {
        if (await payrollLock.LatestLockedPeriodStartAsync(cancellationToken) is not { } latest)
        {
            return null;
        }

        var last = to is { } end && end < latest ? end : latest;
        for (var period = Payroll.PayPeriod.For(from); period.Start <= last; period = period.Next())
        {
            if (await payrollLock.IsLockedAsync(period.Start, cancellationToken))
            {
                return period.Start;
            }
        }

        return null;
    }
}
