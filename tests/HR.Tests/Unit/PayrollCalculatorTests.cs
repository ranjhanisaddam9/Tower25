using HR.Domain.Absences;
using HR.Domain.Pay;
using HR.Domain.Payroll;
using HR.Domain.People;

namespace HR.Tests.Unit;

/// <summary>SPEC §9 golden cases G1–G10, all at rate 280, with exactly the listed numbers.</summary>
public class PayrollCalculatorTests
{
    private const decimal Rate = 280m;

    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static readonly PayPeriod First = PayPeriod.For(Oct(1));
    private static readonly PayPeriod Second = PayPeriod.For(Oct(16));
    private static readonly EmploymentSpan[] Always = [new(new DateOnly(2025, 1, 6), null)];

    private static readonly PayTerms G1Terms = new(Oct(1), 300m, 25m, 300m, PayCurrency.USD);
    private static readonly PayTerms BudgetTerms = new(Oct(1), 1000m, 0m, 196_000m, PayCurrency.PKR);
    private static readonly PayTerms OwnerTerms = new(Oct(1), 1200m, 0m, 1200m, PayCurrency.USD);

    private static readonly AbsenceDay[] BilalAbsences =
    [
        new(Oct(6), AbsencePortion.Full), new(Oct(20), AbsencePortion.Full), new(Oct(27), AbsencePortion.Full),
    ];

    private static PayrollLineResult Calc(
        PayPeriod period,
        PayTerms? terms,
        HireSource? source,
        EmploymentSpan[]? employment = null,
        AbsenceDay[]? absences = null,
        decimal extraDays = 0m,
        AdjustmentInput[]? adjustments = null,
        decimal? rate = Rate) =>
        PayrollCalculator.Calculate(new PayrollInput(period, employment ?? Always, absences ?? [], terms, source, rate, extraDays, adjustments ?? []));

    private static void AssertBase(PayrollLineResult r, decimal payable, int working, decimal billed, decimal payUsd, decimal payPkr, decimal ownerUsd, decimal ownerPkr)
    {
        Assert.Equal(working, r.WorkingDays);
        Assert.Equal(payable, r.PayableDays);
        Assert.Equal(billed, r.BilledUsd);
        Assert.Equal(payUsd, r.PayUsd);
        Assert.Equal(payPkr, r.PayPkr);
        Assert.Equal(ownerUsd, r.OwnerEarningUsd);
        Assert.Equal(ownerPkr, r.OwnerEarningPkr);
        Assert.Null(r.Issue);
    }

    [Fact]
    public void G1_company_recommended_full_period()
    {
        var r = Calc(First, G1Terms, HireSource.CompanyRecommended);
        AssertBase(r, 11m, 11, 175.00m, 150.00m, 42_000m, 25.00m, 7_000m);
        Assert.Equal((150.00m, 25.00m), (r.SalaryPartUsd, r.CommissionUsd));
        Assert.Equal((42_000m, 150.00m, 175.00m), (r.NetPayPkr, r.NetPayUsd, r.InvoiceUsd)); // no adjustments: net = base
    }

    [Fact]
    public void G2_joined_Thursday_Oct_8() =>
        AssertBase(Calc(First, G1Terms, HireSource.CompanyRecommended, [new(Oct(8), null)]), 6m, 11, 95.46m, 81.82m, 22_910m, 13.64m, 3_819m);

    [Fact]
    public void G3_half_and_full_absence()
    {
        var r = Calc(First, G1Terms, HireSource.CompanyRecommended, absences: [new(Oct(5), AbsencePortion.Half), new(Oct(7), AbsencePortion.Full)]);
        AssertBase(r, 10.5m, 11, 167.04m, 143.18m, 40_090m, 23.86m, 6_681m);
        Assert.Equal(0.5m, r.UnpaidDays);
        Assert.Equal([(0.5m, 0m), (0.5m, 0.5m)], r.PeriodAbsences.Select(a => (a.PaidDays, a.UnpaidDays)).ToArray());
    }

    [Fact]
    public void G4_budget_hire_first_period_uses_the_paid_day() =>
        AssertBase(Calc(First, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences), 11m, 11, 500.00m, 350.00m, 98_000m, 150.00m, 42_000m);

    [Fact]
    public void G5_budget_hire_second_period_two_unpaid_days()
    {
        var r = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences);
        AssertBase(r, 9m, 11, 409.09m, 286.36m, 80_182m, 122.73m, 34_363m);
        Assert.Equal((2m, 0m), (r.UnpaidDays, r.ExtraDays));
    }

    [Fact]
    public void G6_owner_full_period() =>
        AssertBase(Calc(Second, OwnerTerms, HireSource.Owner), 11m, 11, 600.00m, 600.00m, 168_000m, 0.00m, 0m);

    [Fact]
    public void G7_leaving_Wednesday_Oct_21() =>
        AssertBase(Calc(Second, G1Terms, HireSource.CompanyRecommended, [new(new DateOnly(2025, 1, 6), Oct(21))]), 4m, 11, 63.64m, 54.55m, 15_274m, 9.09m, 2_545m);

    private static readonly AdjustmentInput[] G8Adjustments =
    [
        new(AdjustmentType.Reimbursement, 5_000m, PayCurrency.PKR),
        new(AdjustmentType.Bonus, 20m, PayCurrency.USD),
        new(AdjustmentType.Deduction, 1_000m, PayCurrency.PKR),
    ];

    [Fact]
    public void G8_adjustments_pass_through_at_cost()
    {
        var r = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences, adjustments: G8Adjustments);

        Assert.Equal(9m, r.PayableDays);
        Assert.Equal(409.09m, r.BilledUsd);
        Assert.Equal(443.38m, r.InvoiceUsd);
        Assert.Equal(89_782m, r.NetPayPkr);
        Assert.Equal(320.65m, r.NetPayUsd);
        Assert.Equal(122.73m, r.OwnerEarningUsd);
        Assert.Equal(34_364m, r.OwnerEarningPkr);

        // Base pay is untouched; each adjustment converted at 280.
        Assert.Equal((286.36m, 80_182m), (r.PayUsd, r.PayPkr));
        Assert.Equal([(5_000m, 17.86m), (5_600m, 20m), (1_000m, 3.57m)], r.AdjustmentAmounts.Select(a => (a.AmountPkr!.Value, a.AmountUsd!.Value)).ToArray());
        Assert.Equal((9_600m, 34.29m), (r.AdjustmentsPkr, r.AdjustmentsUsd));
    }

    [Fact]
    public void G8_owner_earning_USD_equals_the_G5_base_while_PKR_differs_by_one_rupee()
    {
        var g5 = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences);
        var g8 = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences, adjustments: G8Adjustments);

        Assert.Equal(g5.OwnerEarningUsd, g8.OwnerEarningUsd);
        Assert.Equal(g5.BilledUsd - g5.PayUsd, g8.OwnerEarningUsd);
        Assert.Equal(1m, g8.OwnerEarningPkr - g5.OwnerEarningPkr);
        Assert.Equal(g8.InvoiceUsd - g5.BilledUsd, g8.NetPayUsd - g5.PayUsd); // the invoice moves exactly with pay
    }

    [Fact]
    public void G9_one_extra_day()
    {
        var r = Calc(First, G1Terms, HireSource.CompanyRecommended, extraDays: 1m);
        AssertBase(r, 12m, 11, 190.91m, 163.64m, 45_819m, 27.27m, 7_636m);
        Assert.Equal((163.64m, 27.27m), (r.SalaryPartUsd, r.CommissionUsd));
    }

    [Fact]
    public void G10_one_and_a_half_extra_days_on_the_G5_person()
    {
        var r = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences, extraDays: 1.5m);
        AssertBase(r, 10.5m, 11, 477.27m, 334.09m, 93_545m, 143.18m, 40_091m);
        Assert.Equal((2m, 1.5m), (r.UnpaidDays, r.ExtraDays));
    }

    [Fact]
    public void Owner_income_Oct_16_31_is_747_73()
    {
        var lines = new (HireSource?, PayrollLineResult)[]
        {
            (HireSource.Owner, Calc(Second, OwnerTerms, HireSource.Owner)),
            (HireSource.CompanyRecommended, Calc(Second, G1Terms, HireSource.CompanyRecommended)),
            (HireSource.BudgetHire, Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences)),
        };

        Assert.Equal(747.73m, OwnerIncome.Usd(lines));
    }

    // ---------- Issues ----------

    [Fact]
    public void No_hire_source_is_an_issue()
    {
        var r = Calc(First, null, null);
        Assert.Equal(LineIssue.NoHireSource, r.Issue);
        Assert.Null(r.PayPkr);
        Assert.Equal(11, r.EmployedWorkingDays); // days are still counted
    }

    [Fact]
    public void No_rate_record_at_period_start_is_an_issue()
    {
        Assert.Equal(LineIssue.NoRateRecordAtPeriodStart, Calc(First, null, HireSource.BudgetHire).Issue);
    }

    [Fact]
    public void A_record_starting_Oct_16_does_not_cover_an_Oct_1_15_line()
    {
        PayTerms[] records = [G1Terms with { EffectiveFrom = Oct(16) }];

        var first = Calc(First, RateRecords.InEffectOn(records, First.Start), HireSource.CompanyRecommended);
        var second = Calc(Second, RateRecords.InEffectOn(records, Second.Start), HireSource.CompanyRecommended);

        Assert.Equal(LineIssue.NoRateRecordAtPeriodStart, first.Issue);
        Assert.Null(second.Issue);
        Assert.Equal(175.00m, second.BilledUsd);
    }

    // ---------- Extra days and adjustments limits ----------

    [Theory]
    [InlineData("0", false)]
    [InlineData("0.25", false)]
    [InlineData("10.5", false)]
    [InlineData("-1", false)]
    [InlineData("0.5", true)]
    [InlineData("2.5", true)]
    [InlineData("10", true)]
    public void Extra_days_limits(string value, bool valid)
    {
        var days = decimal.Parse(value, CultureInfo.InvariantCulture);
        Assert.Equal(valid, ExtraDaysRules.Error(days) is null);
    }

    [Fact]
    public void The_calculator_refuses_invalid_extra_days() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Calc(First, G1Terms, HireSource.CompanyRecommended, extraDays: 0.25m));

    [Theory]
    [InlineData("0.01", "USD", true)]
    [InlineData("100000", "USD", true)]
    [InlineData("0", "USD", false)]
    [InlineData("100000.01", "USD", false)]
    [InlineData("1.005", "USD", false)]
    [InlineData("1", "PKR", true)]
    [InlineData("50000000", "PKR", true)]
    [InlineData("0.5", "PKR", false)]
    [InlineData("50000001", "PKR", false)]
    [InlineData("10.5", "PKR", false)]
    public void Adjustment_amount_limits(string amount, string currency, bool valid) =>
        Assert.Equal(valid, AdjustmentRules.AmountError(decimal.Parse(amount, CultureInfo.InvariantCulture), Enum.Parse<PayCurrency>(currency)) is null);

    // ---------- Exchange rate ----------

    [Fact]
    public void Changing_the_rate_recomputes_the_PKR_values()
    {
        var at280 = Calc(First, G1Terms, HireSource.CompanyRecommended, adjustments: [new(AdjustmentType.Bonus, 10m, PayCurrency.USD)]);
        var at300 = Calc(First, G1Terms, HireSource.CompanyRecommended, adjustments: [new(AdjustmentType.Bonus, 10m, PayCurrency.USD)], rate: 300m);

        Assert.Equal((150m, 175m), (at300.PayUsd, at300.BilledUsd)); // USD amounts don't move
        Assert.Equal((42_000m, 45_000m), (at280.PayPkr, at300.PayPkr));
        Assert.Equal((44_800m, 48_000m), (at280.NetPayPkr, at300.NetPayPkr));
        Assert.Equal(round0(185m * 300m) - 48_000m, at300.OwnerEarningPkr);

        // PKR pay: the USD side moves instead.
        var budget = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences, rate: 300m);
        Assert.Equal((80_182m, 267.27m), (budget.PayPkr, budget.PayUsd));

        static decimal round0(decimal x) => Money.RoundPkr(x);
    }

    [Fact]
    public void Without_a_rate_only_the_unconverted_amounts_are_known()
    {
        var r = Calc(Second, BudgetTerms, HireSource.BudgetHire, absences: BilalAbsences, adjustments: [new(AdjustmentType.Bonus, 20m, PayCurrency.USD)], rate: null);

        Assert.Equal((409.09m, 80_182m), (r.BilledUsd, r.PayPkr));
        Assert.Null(r.PayUsd);
        Assert.Null(r.NetPayPkr);
        Assert.Equal(429.09m, r.InvoiceUsd); // a USD adjustment needs no rate: 409.09 + 20.00
        Assert.Null(r.OwnerEarningPkr);
        Assert.Null(r.Issue); // a missing rate blocks the whole run, not the line
    }
}
