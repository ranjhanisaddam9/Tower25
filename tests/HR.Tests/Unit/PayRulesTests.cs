using HR.Domain.Pay;
using HR.Domain.People;

namespace HR.Tests.Unit;

public class RateRecordLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static RateRecord Record(string effectiveFrom, decimal salary) =>
        RateRecord.Create(1, new PayTerms(DateOnly.Parse(effectiveFrom, CultureInfo.InvariantCulture), salary, 50m, salary + 50m), null, false, "a", Now);

    [Fact]
    public void A_record_effective_Oct_16_applies_to_Oct_16_31_but_not_Oct_1_15()
    {
        RateRecord[] records = [Record("2026-10-01", 300m), Record("2026-10-16", 330m)];

        Assert.Equal(300m, RateRecords.InEffectOn(records, new DateOnly(2026, 10, 1))!.SalaryMonthlyUsd); // period start Oct 1
        Assert.Equal(330m, RateRecords.InEffectOn(records, new DateOnly(2026, 10, 16))!.SalaryMonthlyUsd); // period start Oct 16
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

/// <summary>SPEC §2 (v1.1.0): which figures the Admin enters per hire source; the third is derived, posted values ignored.</summary>
public class PayDerivationTests
{
    private static readonly DateOnly Earliest = new(2026, 1, 1);
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    // Every field is posted, including ones that don't apply: the derivation must ignore them.
    private static AdminPayInput Tampered(decimal? salary = 300m, decimal? commission = 50m, decimal? budget = 9_999m) =>
        new(Oct1, salary, commission, budget);

    [Fact]
    public void CompanyRecommended_enters_salary_and_commission_and_billed_is_their_sum()
    {
        var terms = PayRules.DeriveAdmin(HireSource.CompanyRecommended, Tampered(), Earliest, out var errors)!;

        Assert.Empty(errors);
        Assert.Equal(new PayTerms(Oct1, 300m, 50m, 350m), terms); // the posted budget is ignored
    }

    [Fact]
    public void BudgetHire_enters_budget_and_salary_and_commission_is_the_difference()
    {
        var terms = PayRules.DeriveAdmin(HireSource.BudgetHire, Tampered(salary: 700m, budget: 1_000m, commission: 99m), Earliest, out _)!;

        Assert.Equal(new PayTerms(Oct1, 700m, 300m, 1_000m), terms); // the posted commission is ignored
    }

    [Fact]
    public void Owner_enters_the_salary_only()
    {
        var terms = PayRules.DeriveAdmin(HireSource.Owner, Tampered(salary: 1_200m, commission: 77m, budget: 5_000m), Earliest, out _)!;

        Assert.Equal(new PayTerms(Oct1, 1_200m, 0m, 1_200m), terms);
    }

    [Fact]
    public void A_BudgetHire_salary_above_the_budget_derives_a_negative_commission_that_loses_money()
    {
        var terms = PayRules.DeriveAdmin(HireSource.BudgetHire, Tampered(salary: 1_100m, budget: 1_000m), Earliest, out _)!;

        Assert.Equal(-100m, terms.CommissionMonthlyUsd);
        Assert.True(PayRules.LosesMoney(terms));
        Assert.False(PayRules.LosesMoney(terms with { SalaryMonthlyUsd = 1_000m, CommissionMonthlyUsd = 0m })); // no margin is not a loss
    }

    [Theory]
    [InlineData(HireSource.CompanyRecommended, "Salary")]
    [InlineData(HireSource.CompanyRecommended, "Commission")]
    [InlineData(HireSource.BudgetHire, "Budget")]
    [InlineData(HireSource.BudgetHire, "Salary")]
    [InlineData(HireSource.Owner, "Salary")]
    public void Each_entered_figure_is_required(HireSource source, string field)
    {
        var input = new AdminPayInput(Oct1,
            field == "Salary" ? null : 700m,
            field == "Commission" ? null : 50m,
            field == "Budget" ? null : 1_000m);

        Assert.Null(PayRules.DeriveAdmin(source, input, Earliest, out var errors));
        Assert.Contains(errors, e => e.Field == field);
    }

    [Theory]
    [InlineData(HireSource.CompanyRecommended)]
    [InlineData(HireSource.BudgetHire)]
    [InlineData(HireSource.Owner)]
    public void Manager_increments_follow_the_source_rules(HireSource source)
    {
        var basis = source switch
        {
            HireSource.CompanyRecommended => new PayTerms(new DateOnly(2026, 4, 1), 300m, 50m, 350m),
            HireSource.BudgetHire => new PayTerms(new DateOnly(2026, 4, 1), 700m, 300m, 1_000m),
            _ => new PayTerms(new DateOnly(2026, 4, 1), 1_200m, 0m, 1_200m),
        };

        var terms = PayRules.DeriveIncrement(source, basis, new DateOnly(2026, 10, 16), 800m, Earliest, out var errors)!;

        Assert.Empty(errors);
        Assert.Equal(800m, terms.SalaryMonthlyUsd);
        switch (source)
        {
            case HireSource.CompanyRecommended:
                Assert.Equal((50m, 850m), (terms.CommissionMonthlyUsd, terms.BilledMonthlyUsd)); // commission carried, billed re-derived
                break;
            case HireSource.BudgetHire:
                Assert.Equal((200m, 1_000m), (terms.CommissionMonthlyUsd, terms.BilledMonthlyUsd)); // budget carried, commission re-derived
                break;
            default:
                Assert.Equal((0m, 800m), (terms.CommissionMonthlyUsd, terms.BilledMonthlyUsd));
                break;
        }
    }

    [Fact]
    public void A_Manager_increment_above_the_budget_is_accepted_for_the_Admin_to_review()
    {
        var basis = new PayTerms(new DateOnly(2026, 4, 1), 700m, 300m, 1_000m);
        var terms = PayRules.DeriveIncrement(HireSource.BudgetHire, basis, new DateOnly(2026, 10, 16), 1_050m, Earliest, out var errors)!;

        Assert.Empty(errors);
        Assert.Equal(-50m, terms.CommissionMonthlyUsd);
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
    private static PayTerms T(decimal salary, decimal commission) => new(new DateOnly(2026, 10, 1), salary, commission, salary + commission);

    [Fact]
    public void The_first_record_is_Initial() => Assert.Equal(RateChangeType.Initial, PayRules.DeriveChangeType(null, T(300, 50)));

    [Fact]
    public void Salary_up_is_an_Increment_and_down_a_Decrement()
    {
        Assert.Equal(RateChangeType.Increment, PayRules.DeriveChangeType(T(300, 50), T(330, 50)));
        Assert.Equal(RateChangeType.Decrement, PayRules.DeriveChangeType(T(300, 50), T(280, 50)));
    }

    [Fact]
    public void Same_salary_but_commission_or_billed_changed_is_a_BillingChange()
    {
        Assert.Equal(RateChangeType.BillingChange, PayRules.DeriveChangeType(T(700, 300), T(700, 400)));
        Assert.Equal(RateChangeType.Correction, PayRules.DeriveChangeType(T(700, 300), T(700, 300)));
    }
}

public class PayValidationTests
{
    private static readonly DateOnly Earliest = new(2026, 10, 1); // joined Oct 8 → earliest Oct 1

    private static IReadOnlyList<PayError> Errors(DateOnly? effectiveFrom, decimal salary = 300m)
    {
        PayRules.DeriveAdmin(HireSource.Owner, new AdminPayInput(effectiveFrom, salary, null, null), Earliest, out var errors);
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
    [InlineData("-0.01", false)]
    [InlineData("0", true)]
    [InlineData("20000", true)]
    [InlineData("20000.01", false)]
    [InlineData("50.005", false)]
    public void CompanyRecommended_commission_limits(string commission, bool valid)
    {
        var terms = PayRules.DeriveAdmin(HireSource.CompanyRecommended,
            new AdminPayInput(new DateOnly(2026, 10, 1), 300m, decimal.Parse(commission, CultureInfo.InvariantCulture), null), Earliest, out _);
        Assert.Equal(valid, terms is not null);
    }

    [Fact]
    public void Billed_must_equal_salary_plus_commission()
    {
        var errors = PayRules.TermErrors(new PayTerms(new DateOnly(2026, 10, 1), 300m, 50m, 400m));
        Assert.Contains(errors, e => e.Message == "Billed must equal salary plus commission.");
    }

    [Fact]
    public void The_entity_refuses_invalid_terms_even_if_validation_is_bypassed()
    {
        Assert.Throws<ArgumentException>(() => RateRecord.Create(1, new PayTerms(new DateOnly(2026, 10, 8), 300m, 50m, 350m), null, false, "a", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => RateRecord.Create(1, new PayTerms(new DateOnly(2026, 10, 1), 300m, 50m, 999m), null, false, "a", DateTimeOffset.UtcNow));
    }
}

public class PayMathTests
{
    [Fact]
    public void Budget_hire_full_period_matches_golden_G4()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 1), 700m, 300m, 1_000m), 280m);

        Assert.Equal((350.00m, 150.00m, 500.00m), (amounts.SalaryUsd, amounts.CommissionUsd, amounts.BilledUsd));
        Assert.Equal(98_000m, amounts.PayPkr);
        Assert.Equal(150.00m, amounts.EarningUsd);
    }

    [Fact]
    public void CompanyRecommended_full_period_matches_golden_G1()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 1), 300m, 50m, 350m), 280m);

        Assert.Equal((150.00m, 25.00m, 175.00m), (amounts.SalaryUsd, amounts.CommissionUsd, amounts.BilledUsd));
        Assert.Equal(42_000m, amounts.PayPkr);
        Assert.Equal(25.00m, amounts.EarningUsd);
    }

    [Fact]
    public void Owner_full_period_earns_the_whole_billed_amount()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 16), 1_200m, 0m, 1_200m), 280m, HireSource.Owner);

        Assert.Equal(600.00m, amounts.BilledUsd);
        Assert.Equal(168_000m, amounts.PayPkr);
        Assert.Equal(600.00m, amounts.EarningUsd);
    }

    [Fact]
    public void Without_a_rate_the_USD_figures_are_known_and_the_PKR_estimate_is_not()
    {
        var amounts = PayMath.FullPeriod(new PayTerms(new DateOnly(2026, 10, 1), 700m, 300m, 1_000m), null);

        Assert.Equal((350.00m, 150.00m), (amounts.SalaryUsd, amounts.EarningUsd));
        Assert.Null(amounts.PayPkr);
    }
}
