using HR.Domain.Absences;
using HR.Domain.Payroll;
using HR.Domain.People;

namespace HR.Tests.Unit;

public class PaidLeaveAllocatorTests
{
    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static AbsenceDay Full(DateOnly date) => new(date, AbsencePortion.Full);

    private static AbsenceDay Half(DateOnly date) => new(date, AbsencePortion.Half);

    private static (decimal Paid, decimal Unpaid) Split(IReadOnlyList<AllocatedAbsence> result, DateOnly date)
    {
        var a = result.Single(r => r.Date == date);
        return (a.PaidDays, a.UnpaidDays);
    }

    [Fact]
    public void G3_a_half_day_uses_half_the_leave_and_the_next_full_day_is_split()
    {
        var result = PaidLeaveAllocator.Allocate([Half(Oct(5)), Full(Oct(7))]);

        Assert.Equal((0.5m, 0m), Split(result, Oct(5)));
        Assert.Equal((0.5m, 0.5m), Split(result, Oct(7)));
        Assert.True(result[1].IsPartlyPaid);
    }

    [Fact]
    public void G4_G5_the_first_absence_of_the_month_uses_the_leave_across_both_periods()
    {
        var result = PaidLeaveAllocator.Allocate([Full(Oct(6)), Full(Oct(20)), Full(Oct(27))]);

        Assert.Equal((1m, 0m), Split(result, Oct(6)));
        Assert.Equal((0m, 1m), Split(result, Oct(20)));
        Assert.Equal((0m, 1m), Split(result, Oct(27)));
        Assert.True(result[0].IsFullyPaid);
        Assert.True(result[2].IsUnpaid);
    }

    [Fact]
    public void Leave_resets_at_the_month_boundary()
    {
        var result = PaidLeaveAllocator.Allocate([Full(Oct(30)), Full(new DateOnly(2026, 11, 2))]);

        Assert.All(result, a => Assert.Equal((1m, 0m), (a.PaidDays, a.UnpaidDays)));
    }

    [Fact]
    public void Two_halves_are_paid_and_a_third_half_is_unpaid()
    {
        var result = PaidLeaveAllocator.Allocate([Half(Oct(1)), Half(Oct(2)), Half(Oct(5))]);

        Assert.Equal((0.5m, 0m), Split(result, Oct(1)));
        Assert.Equal((0.5m, 0m), Split(result, Oct(2)));
        Assert.Equal((0m, 0.5m), Split(result, Oct(5)));
    }

    [Fact]
    public void The_result_does_not_depend_on_input_order()
    {
        AbsenceDay[] ordered = [Half(Oct(1)), Full(Oct(6)), Full(Oct(20)), Half(Oct(29)), Full(new DateOnly(2026, 11, 3)), Full(new DateOnly(2026, 9, 30))];
        var expected = PaidLeaveAllocator.Allocate(ordered);

        var random = new Random(20261009);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = ordered.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, PaidLeaveAllocator.Allocate(shuffled));
        }

        Assert.Equal(ordered.Select(a => a.Date).Order(), expected.Select(a => a.Date));
    }

    [Fact]
    public void Deleting_the_first_absence_makes_the_next_one_paid()
    {
        var before = PaidLeaveAllocator.Allocate([Full(Oct(6)), Full(Oct(20))]);
        Assert.Equal((0m, 1m), Split(before, Oct(20)));

        var after = PaidLeaveAllocator.Allocate([Full(Oct(20))]);
        Assert.Equal((1m, 0m), Split(after, Oct(20)));
    }

    [Fact]
    public void A_rehire_in_the_same_month_continues_the_same_allowance()
    {
        // Employed Oct 1–9 and again from Oct 19: leave is per person per month, not per employment period.
        var result = PaidLeaveAllocator.Allocate([Half(Oct(8)), Full(Oct(21))]);

        Assert.Equal((0.5m, 0m), Split(result, Oct(8)));
        Assert.Equal((0.5m, 0.5m), Split(result, Oct(21)));
    }

    [Fact]
    public void Leave_left_in_a_month_counts_only_that_month()
    {
        AbsenceDay[] absences = [Half(Oct(5)), Full(new DateOnly(2026, 9, 7))];

        Assert.Equal(0.5m, PaidLeaveAllocator.LeftInMonth(absences, Oct(1)));
        Assert.Equal(0m, PaidLeaveAllocator.LeftInMonth(absences, new DateOnly(2026, 9, 1)));
        Assert.Equal(1m, PaidLeaveAllocator.LeftInMonth(absences, new DateOnly(2026, 11, 1)));
        Assert.Equal(0m, PaidLeaveAllocator.LeftInMonth([Full(Oct(1)), Full(Oct(2))], Oct(1)));
    }

    [Fact]
    public void No_absences_allocate_to_nothing()
    {
        Assert.Empty(PaidLeaveAllocator.Allocate([]));
    }
}

public class PayableDaysTests
{
    private static DateOnly Oct(int day) => new(2026, 10, day);

    private static readonly PayPeriod First = PayPeriod.For(new DateOnly(2026, 10, 1));
    private static readonly PayPeriod Second = PayPeriod.For(new DateOnly(2026, 10, 16));
    private static readonly EmploymentSpan[] AllOctober = [new(new DateOnly(2025, 1, 6), null)];

    private static PayableDaysResult Compute(EmploymentSpan[] employment, PayPeriod period, params AbsenceDay[] absences) =>
        PayableDays.For(employment, PaidLeaveAllocator.Allocate(absences), period);

    private static void AssertDays(PayableDaysResult result, decimal payable, int working)
    {
        Assert.Equal(working, result.WorkingDays);
        Assert.Equal(payable, result.PayableDays);
    }

    [Fact]
    public void G1_full_period_no_absences() => AssertDays(Compute(AllOctober, First), 11m, 11);

    [Fact]
    public void G2_joined_Thursday_Oct_8()
    {
        var result = Compute([new(Oct(8), null)], First);
        AssertDays(result, 6m, 11);
        Assert.Equal(6, result.EmployedWorkingDays);
    }

    [Fact]
    public void G3_half_and_full_absence() =>
        AssertDays(Compute(AllOctober, First, new(Oct(5), AbsencePortion.Half), new(Oct(7), AbsencePortion.Full)), 10.5m, 11);

    [Fact]
    public void G4_first_period_uses_the_paid_day() =>
        AssertDays(Compute(AllOctober, First, new(Oct(6), AbsencePortion.Full), new(Oct(20), AbsencePortion.Full), new(Oct(27), AbsencePortion.Full)), 11m, 11);

    [Fact]
    public void G5_second_period_has_two_unpaid_days()
    {
        var result = Compute(AllOctober, Second, new(Oct(6), AbsencePortion.Full), new(Oct(20), AbsencePortion.Full), new(Oct(27), AbsencePortion.Full));
        AssertDays(result, 9m, 11);
        Assert.Equal(2m, result.UnpaidDays);
    }

    [Fact]
    public void G6_full_period() => AssertDays(Compute(AllOctober, Second), 11m, 11);

    [Fact]
    public void G7_leaving_Wednesday_Oct_21() => AssertDays(Compute([new(new DateOnly(2025, 1, 6), Oct(21))], Second), 4m, 11);

    [Fact]
    public void Two_employment_periods_in_one_month()
    {
        EmploymentSpan[] twoStints = [new(Oct(1), Oct(9)), new(Oct(19), Oct(31))];

        AssertDays(Compute(twoStints, First), 7m, 11);
        AssertDays(Compute(twoStints, Second), 10m, 11);
    }

    [Fact]
    public void Absences_outside_employment_or_the_period_are_ignored()
    {
        // Oct 12 is after the leaving date; Oct 20 is in the other period.
        var result = Compute([new(Oct(1), Oct(9))], First, new(Oct(2), AbsencePortion.Full), new(Oct(5), AbsencePortion.Full), new(Oct(12), AbsencePortion.Full), new(Oct(20), AbsencePortion.Full));

        Assert.Equal(7, result.EmployedWorkingDays);
        Assert.Equal(1m, result.UnpaidDays); // Oct 2 paid, Oct 5 unpaid
        Assert.Equal(6m, result.PayableDays);
    }
}

public class AbsenceRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly EmploymentSpan[] Employed = [new(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 21)), new(new DateOnly(2026, 11, 2), null)];

    [Theory]
    [InlineData("2026-10-10")] // Saturday
    [InlineData("2026-10-11")] // Sunday
    public void Weekends_are_refused(string date) =>
        Assert.Equal(AbsenceRules.WeekendMessage, AbsenceRules.DateError(DateOnly.Parse(date, CultureInfo.InvariantCulture), Today, Employed, false));

    [Theory]
    [InlineData("2026-08-31")] // before joining
    [InlineData("2026-10-22")] // after leaving
    [InlineData("2026-10-30")] // between periods
    public void Dates_outside_employment_are_refused(string date) =>
        Assert.Equal(AbsenceRules.NotEmployedMessage, AbsenceRules.DateError(DateOnly.Parse(date, CultureInfo.InvariantCulture), Today, Employed, false));

    [Fact]
    public void Duplicates_are_refused() =>
        Assert.Equal(AbsenceRules.DuplicateMessage, AbsenceRules.DateError(new DateOnly(2026, 10, 7), Today, Employed, alreadyRecorded: true));

    [Fact]
    public void At_most_one_year_ahead()
    {
        // Oct 9 2027 is a Saturday; Oct 8 2027 (Friday) is exactly a year minus a day; Oct 11 2027 is too far.
        Assert.Null(AbsenceRules.DateError(new DateOnly(2027, 10, 8), Today, Employed, false));
        Assert.Equal(AbsenceRules.TooFarAheadMessage, AbsenceRules.DateError(new DateOnly(2027, 10, 11), Today, Employed, false));
    }

    [Fact]
    public void A_valid_date_passes() => Assert.Null(AbsenceRules.DateError(new DateOnly(2026, 10, 7), Today, Employed, false));

    [Fact]
    public void Entity_refuses_weekends_long_notes_and_unknown_portions()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.Throws<ArgumentException>(() => Absence.Create(1, new DateOnly(2026, 10, 10), AbsencePortion.Full, null, "a", now));
        Assert.Throws<ArgumentException>(() => Absence.Create(1, new DateOnly(2026, 10, 9), AbsencePortion.Full, new string('x', 301), "a", now));
        Assert.Throws<ArgumentOutOfRangeException>(() => Absence.Create(1, new DateOnly(2026, 10, 9), (AbsencePortion)7, null, "a", now));

        var absence = Absence.Create(1, new DateOnly(2026, 10, 9), AbsencePortion.Half, "  sick  ", "a", now);
        Assert.Equal(("sick", AbsencePortion.Half), (absence.Note, absence.Portion));
    }

    [Theory]
    [InlineData("Full", true, AbsencePortion.Full)]
    [InlineData("half", true, AbsencePortion.Half)]
    [InlineData("1", false, default(AbsencePortion))]
    [InlineData("Present", false, default(AbsencePortion))]
    [InlineData(null, false, default(AbsencePortion))]
    public void Portion_parsing_accepts_names_only(string? value, bool ok, AbsencePortion expected)
    {
        Assert.Equal(ok, AbsencePortions.TryParse(value, out var portion));
        Assert.Equal(expected, portion);
    }
}
