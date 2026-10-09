using HR.Domain.Pay;
using HR.Domain.People;

namespace HR.Tests.Unit;

public class RateRecordLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static RateRecord Record(string effectiveFrom, decimal pay) =>
        RateRecord.Create(1, new PayTerms(DateOnly.Parse(effectiveFrom, CultureInfo.InvariantCulture), pay, 25m, pay, PayCurrency.USD), null, false, "a", Now);

    [Fact]
    public void A_record_effective_Oct_16_applies_to_Oct_16_31_but_not_Oct_1_15()
    {
        RateRecord[] records = [Record("2026-10-01", 300m), Record("2026-10-16", 330m)];

        Assert.Equal(300m, RateRecords.InEffectOn(records, new DateOnly(2026, 10, 1))!.PayMonthlyAmount); // period start Oct 1
        Assert.Equal(330m, RateRecords.InEffectOn(records, new DateOnly(2026, 10, 16))!.PayMonthlyAmount); // period start Oct 16
        Assert.Null(RateRecords.InEffectOn(records, new DateOnly(2026, 9, 16)));
    }

    [Fact]
    public void An_Initial_record_effective_Oct_1_covers_a_person_joining_Oct_8()
    {
        // Payroll looks up InEffectOn(period.Start): for Oct 1–15 that is Oct 1, even though the person joined Oct 8.
        var earliest = PayRules.EarliestEffectiveFrom(new DateOnly(2026, 10, 8));
        Assert.Equal(new DateOnly(2026, 10, 1), earliest);

        RateRecord[] records = [Record("2026-10-01", 300m)];
        Assert.NotNull(RateRecords.InEffectOn(records, new DateOnly(2026, 10, 1)));
    }
}

public class PayDerivationTests
{
    private static readonly DateOnly Earliest = new(2026, 1, 1);

    // Every field is posted, including ones that don't apply: the derivation must ignore them.
    private static AdminPayInput Tampered(decimal? salary = 300m, decimal? commission = 25m, decimal? budget = 9_999m, decimal? pay = 4_444m, PayCurrency? currency = PayCurrency.PKR) =>
        new(new DateOnly(2026, 10, 1), salary, commission, budget, pay, currency);

    [Fact]
    public void CompanyRecommended_pays_the_billed_salary_in_USD_with_the_commission()
    {
        var terms = PayRules.DeriveAdmin(HireSource.CompanyRecommended, Tampered(), Earliest, out var errors)!;

        Assert.Empty(errors);
        Assert.Equal(new PayTerms(new DateOnly(2026, 10, 1), 300m, 25m, 300m, PayCurrency.USD), terms);
    }

    [Fact]
    public void BudgetHire_bills_the_budget_pays_the_pay_and_never_has_commission()
    {
        var terms = PayRules.DeriveAdmin(HireSource.BudgetHire, Tampered(budget: 1_000m, pay: 196_000m, commission: 99m), Earliest, out _)!;

        Assert.Equal(new PayTerms(new DateOnly(2026, 10, 1), 1_000m, 0m, 196_000m, PayCurrency.PKR), terms);
    }

    [Fact]
    public void BudgetHire_currency_defaults_to_PKR_and_may_be_USD()
    {
        Assert.Equal(PayCurrency.PKR, PayRules.DeriveAdmin(HireSource.BudgetHire, Tampered(budget: 1_000m, pay: 196_000m, currency: null), Earliest, out _)!.PayCurrency);
        Assert.Equal(PayCurrency.USD, PayRules.DeriveAdmin(HireSource.BudgetHire, Tampered(budget: 1_000m, pay: 700m, currency: PayCurrency.USD), Earliest, out _)!.PayCurrency);
    }

    [Fact]
    public void Owner_is_billed_and_paid_the_same_USD_salary_without_commission()
    {
        var terms = PayRules.DeriveAdmin(HireSource.Owner, Tampered(salary: 1_200m), Earliest, out _)!;

        Assert.Equal(new PayTerms(new DateOnly(2026, 10, 1), 1_200m, 0m, 1_200m, PayCurrency.USD), terms);
    }

    [Theory]
    [InlineData(HireSource.CompanyRecommended)]
    [InlineData(HireSource.BudgetHire)]
    [InlineData(HireSource.Owner)]
    public void Increments_follow_the_source_rules(HireSource source)
    {
        var basis = source switch
        {
            HireSource.CompanyRecommended => new PayTerms(new DateOnly(2026, 4, 1), 300m, 25m, 300m, PayCurrency.USD),
            HireSource.BudgetHire => new PayTerms(new DateOnly(2026, 4, 1), 1_000m, 0m, 196_000m, PayCurrency.PKR),
            _ => new PayTerms(new DateOnly(2026, 4, 1), 1_200m, 0m, 1_200m, PayCurrency.USD),
        };
        var newPay = source == HireSource.BudgetHire ? 210_000m : 330m;

        var terms = PayRules.DeriveIncrement(source, basis, new DateOnly(2026, 10, 16), newPay, Earliest, out var errors)!;

        Assert.Empty(errors);
        Assert.Equal(newPay, terms.PayMonthlyAmount);
        Assert.Equal(basis.PayCurrency, terms.PayCurrency);
        switch (source)
        {
            case HireSource.CompanyRecommended:
                Assert.Equal(newPay, terms.BilledMonthlyUsd);       // billed follows pay
                Assert.Equal(25m, terms.CommissionPerPeriodUsd);    // commission carried over
                break;
            case HireSource.BudgetHire:
                Assert.Equal(1_000m, terms.BilledMonthlyUsd);       // budget unchanged
                Assert.Equal(0m, terms.CommissionPerPeriodUsd);
                break;
            default:
                Assert.Equal(newPay, terms.BilledMonthlyUsd);
                Assert.Equal(0m, terms.CommissionPerPeriodUsd);
                break;
        }
    }

    [Fact]
    public void No_hire_source_means_no_record()
    {
        Assert.Null(PayRules.DeriveAdmin((HireSource)0, Tampered(), Earliest, out var errors));
        Assert.Equal(PayRules.NoHireSourceMessage, Assert.Single(errors).Message);
    }
}

public class ChangeTypeTests
{
    private static PayTerms T(decimal billed, decimal commission, decimal pay, PayCurrency currency = PayCurrency.USD) =>
        new(new DateOnly(2026, 10, 1), billed, commission, pay, currency);

    [Fact]
    public void The_first_record_is_Initial() => Assert.Equal(RateChangeType.Initial, PayRules.DeriveChangeType(null, T(300, 25, 300), null));

    [Fact]
    public void Pay_up_is_an_Increment_and_down_a_Decrement()
    {
        Assert.Equal(RateChangeType.Increment, PayRules.DeriveChangeType(T(300, 25, 300), T(330, 25, 330), null));
        Assert.Equal(RateChangeType.Decrement, PayRules.DeriveChangeType(T(300, 25, 300), T(280, 25, 280), null));
    }

    [Fact]
    public void Same_pay_but_billing_or_commission_changed_is_a_BillingChange()
    {
        Assert.Equal(RateChangeType.BillingChange, PayRules.DeriveChangeType(T(1_000, 0, 196_000, PayCurrency.PKR), T(1_100, 0, 196_000, PayCurrency.PKR), null));
        Assert.Equal(RateChangeType.BillingChange, PayRules.DeriveChangeType(T(300, 25, 300), T(300, 30, 300), null));
    }

    [Fact]
    public void A_currency_switch_compares_in_USD_at_the_rate_or_is_a_Correction_without_one()
    {
        Assert.Equal(RateChangeType.Increment, PayRules.DeriveChangeType(T(1_000, 0, 196_000, PayCurrency.PKR), T(1_000, 0, 750, PayCurrency.USD), 280m)); // 700 → 750 USD
        Assert.Equal(RateChangeType.Correction, PayRules.DeriveChangeType(T(1_000, 0, 196_000, PayCurrency.PKR), T(1_000, 0, 750, PayCurrency.USD), null));
    }
}

public class PayValidationTests
{
    private static readonly DateOnly Earliest = new(2026, 10, 1); // joined Oct 8 → earliest Oct 1

    private static IReadOnlyList<PayError> Errors(DateOnly? effectiveFrom, decimal salary = 300m)
    {
        PayRules.DeriveAdmin(HireSource.Owner, new AdminPayInput(effectiveFrom, salary, null, null, null, null), Earliest, out var errors);
        return errors;
    }

    [Theory]
    [InlineData("2026-10-01", true)]
    [InlineData("2026-10-16", true)]
    [InlineData("2026-10-08", false)] // not a period start
    [InlineData("2026-10-15", false)]
    [InlineData("2026-09-16", false)] // before the period containing the first employment start
    public void EffectiveFrom_must_be_the_1st_or_16th_and_not_too_early(string date, bool valid) =>
        Assert.Equal(valid, Errors(DateOnly.Parse(date, CultureInfo.InvariantCulture)).Count == 0);

    [Fact]
    public void EffectiveFrom_is_required() => Assert.NotEmpty(Errors(null));

    [Theory]
    [InlineData("0.99", false)]
    [InlineData("1", true)]
    [InlineData("100000", true)]
    [InlineData("100000.01", false)]
    [InlineData("300.123", false)] // 3 decimals
    public void USD_monthly_limits(string salary, bool valid) =>
        Assert.Equal(valid, Errors(new DateOnly(2026, 10, 1), decimal.Parse(salary, CultureInfo.InvariantCulture)).Count == 0);

    [Theory]
    [InlineData("999", false)]
    [InlineData("1000", true)]
    [InlineData("50000000", true)]
    [InlineData("50000001", false)]
    [InlineData("196000.50", false)] // PKR fraction
    public void PKR_monthly_limits_and_whole_rupees(string pay, bool valid)
    {
        var terms = PayRules.DeriveAdmin(HireSource.BudgetHire,
            new AdminPayInput(new DateOnly(2026, 10, 1), null, null, 100_000m, decimal.Parse(pay, CultureInfo.InvariantCulture), PayCurrency.PKR), Earliest, out var errors);
        Assert.Equal(valid, terms is not null);
        if (!valid)
        {
            Assert.Contains(errors, e => e.Field == PayFields.Pay);
        }
    }

    [Fact]
    public void A_PKR_fraction_is_rejected_with_a_clear_message()
    {
        var errors = PayRules.TermErrors(new PayTerms(new DateOnly(2026, 10, 1), 1_000m, 0m, 196_000.5m, PayCurrency.PKR));
        Assert.Contains(errors, e => e.Message == "PKR pay must be in whole rupees.");
    }

    [Theory]
    [InlineData("-0.01", false)]
    [InlineData("0", true)]
    [InlineData("10000", true)]
    [InlineData("10000.01", false)]
    public void Commission_limits(string commission, bool valid)
    {
        var terms = PayRules.DeriveAdmin(HireSource.CompanyRecommended,
            new AdminPayInput(new DateOnly(2026, 10, 1), 300m, decimal.Parse(commission, CultureInfo.InvariantCulture), null, null, null), Earliest, out _);
        Assert.Equal(valid, terms is not null);
    }

    [Fact]
    public void The_entity_refuses_invalid_terms_even_if_validation_is_bypassed() =>
        Assert.Throws<ArgumentException>(() => RateRecord.Create(1, new PayTerms(new DateOnly(2026, 10, 8), 300m, 25m, 300m, PayCurrency.USD), null, false, "a", DateTimeOffset.UtcNow));
}

public class PayMathTests
{
    [Fact]
    public void Margin_preview_budget_1000_pay_PKR_196000_at_280_is_150()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 1), 1_000m, 0m, 196_000m, PayCurrency.PKR), 280m);

        Assert.Equal(98_000m, amounts.PayPkr);
        Assert.Equal(350.00m, amounts.PayUsd);     // pay USD 700 a month
        Assert.Equal(500.00m, amounts.BilledUsd);
        Assert.Equal(150.00m, amounts.EarningUsd);
    }

    [Fact]
    public void CompanyRecommended_full_period_matches_golden_G1()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 1), 300m, 25m, 300m, PayCurrency.USD), 280m);

        Assert.Equal(175.00m, amounts.BilledUsd);
        Assert.Equal(150.00m, amounts.PayUsd);
        Assert.Equal(42_000m, amounts.PayPkr);
        Assert.Equal(25.00m, amounts.EarningUsd);
    }

    [Fact]
    public void Owner_full_period_matches_golden_G6()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 16), 1_200m, 0m, 1_200m, PayCurrency.USD), 280m);

        Assert.Equal(600.00m, amounts.BilledUsd);
        Assert.Equal(168_000m, amounts.PayPkr);
        Assert.Equal(0.00m, amounts.EarningUsd);
    }

    [Fact]
    public void Without_a_rate_PKR_pay_has_no_USD_estimate()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 1), 1_000m, 0m, 196_000m, PayCurrency.PKR), null);

        Assert.Equal(98_000m, amounts.PayPkr);
        Assert.Null(amounts.PayUsd);
        Assert.Null(amounts.EarningUsd);
    }

    [Theory]
    [InlineData("1000", "196000", "PKR", "280", true)]    // 700 < 1000
    [InlineData("700", "196000", "PKR", "280", false)]    // 700 ≥ 700: loses money (no margin)
    [InlineData("1000", "1000", "USD", "280", false)]
    [InlineData("1000", "999.99", "USD", "280", true)]
    public void Loss_check_converts_PKR_at_the_rate(string budget, string pay, string currency, string rate, bool profitable)
    {
        var terms = new PayTerms(new DateOnly(2026, 10, 1), decimal.Parse(budget, CultureInfo.InvariantCulture), 0m,
            decimal.Parse(pay, CultureInfo.InvariantCulture), Enum.Parse<PayCurrency>(currency));
        Assert.Equal(!profitable, PayRules.LosesMoney(terms, decimal.Parse(rate, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Loss_check_is_unknown_for_PKR_without_a_rate() =>
        Assert.Null(PayRules.LosesMoney(new PayTerms(new DateOnly(2026, 10, 1), 1_000m, 0m, 196_000m, PayCurrency.PKR), null));
}
