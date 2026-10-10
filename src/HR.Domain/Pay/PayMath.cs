using HR.Domain.Payroll;

namespace HR.Domain.Pay;

/// <summary>Amounts for one FULL pay period (f = 1), rounded exactly as SPEC §5 does.</summary>
/// <param name="PayUsd">Null when pay is in PKR and there is no exchange rate.</param>
/// <param name="PayPkr">Null when pay is in USD and there is no exchange rate.</param>
/// <param name="EarningUsd">BilledUsd − PayUsd; null when PayUsd is unknown.</param>
public sealed record FullPeriodAmounts(
    decimal SalaryPartUsd,
    decimal CommissionUsd,
    decimal BilledUsd,
    decimal? PayUsd,
    decimal? PayPkr,
    decimal? EarningUsd);

public static class PayMath
{
    /// <summary>
    /// SPEC §5 with f = 1: SalaryPart = round2(billed/2), Commission = round2(commission), BilledUsd = their sum;
    /// USD pay: PayUsd = round2(pay/2), PayPkr = round0(PayUsd × rate); PKR pay: PayPkr = round0(pay/2), PayUsd = round2(PayPkr / rate).
    /// </summary>
    public static FullPeriodAmounts FullPeriod(PayTerms terms, decimal? usdToPkr)
    {
        var salaryPart = Money.RoundUsd(terms.BilledMonthlyUsd / 2);
        var commission = Money.RoundUsd(terms.CommissionPerPeriodUsd);
        var billed = salaryPart + commission;
        var rate = usdToPkr is > 0 ? usdToPkr : null;

        decimal? payUsd;
        decimal? payPkr;
        if (terms.PayCurrency == PayCurrency.USD)
        {
            payUsd = Money.RoundUsd(terms.PayMonthlyAmount / 2);
            payPkr = rate is { } r ? Money.RoundPkr(payUsd.Value * r) : null;
        }
        else
        {
            payPkr = Money.RoundPkr(terms.PayMonthlyAmount / 2);
            payUsd = rate is { } r ? Money.RoundUsd(payPkr.Value / r) : null;
        }

        return new FullPeriodAmounts(salaryPart, commission, billed, payUsd, payPkr, payUsd is { } p ? billed - p : null);
    }

    /// <summary>Pay per full period in its own currency: round2 (USD) or round0 (PKR) of half the monthly amount.</summary>
    public static decimal PayPerFullPeriod(decimal monthly, PayCurrency currency) =>
        currency == PayCurrency.USD ? Money.RoundUsd(monthly / 2) : Money.RoundPkr(monthly / 2);

    /// <summary>The other-currency equivalent of a monthly amount at a rate (approximate, for display).</summary>
    public static decimal Convert(decimal amount, PayCurrency from, decimal usdToPkr) =>
        from == PayCurrency.USD ? Money.RoundPkr(amount * usdToPkr) : Money.RoundUsd(amount / usdToPkr);

    /// <summary>Percentage change between two amounts in the same currency (null if not comparable).</summary>
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
