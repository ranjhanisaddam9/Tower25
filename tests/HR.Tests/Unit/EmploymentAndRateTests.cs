using HR.Domain.People;
using HR.Domain.Rates;

namespace HR.Tests.Unit;

public class EmploymentCalendarTests
{
    private static readonly EmploymentSpan[] TwoStints =
    [
        new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 9)),
        new(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 31)),
    ];

    [Fact]
    public void Two_periods_in_October_2026_count_only_days_inside_them()
    {
        Assert.Equal(7, EmploymentCalendar.EmployedWorkingDays(TwoStints, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15)));
        Assert.Equal(10, EmploymentCalendar.EmployedWorkingDays(TwoStints, new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void An_open_period_runs_to_the_end_of_the_range()
    {
        EmploymentSpan[] open = [new(new DateOnly(2026, 10, 8), null)];

        Assert.Equal(6, EmploymentCalendar.EmployedWorkingDays(open, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15)));
        Assert.Equal(11, EmploymentCalendar.EmployedWorkingDays(open, new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 31)));
    }

    [Fact]
    public void No_periods_means_no_employed_days()
    {
        Assert.Equal(0, EmploymentCalendar.EmployedWorkingDays(Array.Empty<EmploymentSpan>(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.False(EmploymentCalendar.IsEmployedOn(Array.Empty<EmploymentSpan>(), new DateOnly(2026, 10, 1)));
    }

    [Theory]
    [InlineData("2026-10-01", true)]
    [InlineData("2026-10-09", true)]   // last day of the first stint
    [InlineData("2026-10-12", false)]  // the gap
    [InlineData("2026-10-19", true)]   // first day of the second stint
    [InlineData("2026-11-02", false)]
    [InlineData("2026-09-30", false)]
    public void IsEmployedOn_checks_every_period(string date, bool expected)
    {
        Assert.Equal(expected, EmploymentCalendar.IsEmployedOn(TwoStints, DateOnly.Parse(date, CultureInfo.InvariantCulture)));
    }
}

public class EmploymentPeriodInvariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static Person NewPerson(DateOnly joined) => Person.Create(1,
        new PersonInput("Test Person", PersonType.Employee, "Engineer", null, "0300-1234567", null, null, null, joined, null), "actor", Now);

    [Fact]
    public void Overlapping_periods_are_rejected()
    {
        var ex = Assert.Throws<PersonRuleException>(() => EmploymentHistory.EnsureValid(
        [
            new EmploymentSpan(new DateOnly(2026, 1, 5), new DateOnly(2026, 3, 31)),
            new EmploymentSpan(new DateOnly(2026, 3, 31), new DateOnly(2026, 6, 30)),
        ]));
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_second_open_period_is_rejected()
    {
        Assert.Throws<PersonRuleException>(() => EmploymentHistory.EnsureValid(
        [
            new EmploymentSpan(new DateOnly(2026, 1, 5), null),
            new EmploymentSpan(new DateOnly(2026, 6, 1), null),
        ]));
    }

    [Fact]
    public void An_end_before_the_start_is_rejected() =>
        Assert.Throws<PersonRuleException>(() => EmploymentHistory.EnsureValid([new EmploymentSpan(new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 30))]));

    [Fact]
    public void Back_to_back_non_overlapping_periods_are_valid() =>
        EmploymentHistory.EnsureValid(
        [
            new EmploymentSpan(new DateOnly(2026, 6, 1), null),
            new EmploymentSpan(new DateOnly(2026, 1, 5), new DateOnly(2026, 5, 31)),
        ]);

    [Fact]
    public void Rejoining_must_be_after_the_previous_end_and_opens_a_new_period()
    {
        var person = NewPerson(new DateOnly(2026, 1, 5));
        person.Deactivate(new DateOnly(2026, 3, 31), "actor", Now);

        Assert.Throws<PersonRuleException>(() => person.Reactivate(new DateOnly(2026, 3, 31), "actor", Now));
        Assert.Single(person.EmploymentPeriods);

        person.Reactivate(new DateOnly(2026, 4, 1), "actor", Now);
        Assert.Equal(2, person.EmploymentPeriods.Count);
        Assert.Single(person.EmploymentPeriods, p => p.IsOpen);
        Assert.Equal(new DateOnly(2026, 4, 1), person.JoiningDate);   // cache = latest period
        Assert.Null(person.LeavingDate);
    }

    [Fact]
    public void Create_opens_the_first_period_and_deactivate_closes_it()
    {
        var person = NewPerson(new DateOnly(2026, 1, 5));
        var period = Assert.Single(person.EmploymentPeriods);
        Assert.True(period.IsOpen);
        Assert.Equal(new DateOnly(2026, 1, 5), period.StartDate);

        person.Deactivate(new DateOnly(2026, 2, 27), "actor", Now);
        Assert.Equal(new DateOnly(2026, 2, 27), period.EndDate);
        Assert.Equal(person.LeavingDate, period.EndDate);
    }

    [Fact]
    public void Editing_the_joining_date_moves_the_latest_period_but_never_before_the_previous_end()
    {
        var person = NewPerson(new DateOnly(2026, 1, 5));
        person.Deactivate(new DateOnly(2026, 3, 31), "actor", Now);
        person.Reactivate(new DateOnly(2026, 6, 1), "actor", Now);

        var input = new PersonInput("Test Person", PersonType.Employee, "Engineer", null, "0300-1234567", null, null, null, new DateOnly(2026, 3, 15), null);
        Assert.Throws<PersonRuleException>(() => person.UpdateDetails(input, "actor", Now));
        Assert.Equal(new DateOnly(2026, 6, 1), person.JoiningDate);

        person.UpdateDetails(input with { JoiningDate = new DateOnly(2026, 4, 1) }, "actor", Now);
        Assert.Equal(new DateOnly(2026, 4, 1), person.EmploymentPeriods.Single(p => p.IsOpen).StartDate);
        Assert.Equal(new DateOnly(2026, 4, 1), person.JoiningDate);
    }
}

public class RateTimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);

    private static readonly ExchangeRate[] Entries =
    [
        ExchangeRate.Create(new DateOnly(2026, 9, 1), 278.50m, null, "a", Now),
        ExchangeRate.Create(new DateOnly(2026, 10, 1), 280.25m, null, "a", Now),
        ExchangeRate.Create(new DateOnly(2026, 11, 1), 282.00m, "scheduled", "a", Now), // future-dated
    ];

    private static decimal? On(string date) =>
        Entries.AsQueryable().RateOn(DateOnly.Parse(date, CultureInfo.InvariantCulture))?.UsdToPkr;

    [Fact]
    public void Before_the_first_entry_there_is_no_rate() => Assert.Null(On("2026-08-31"));

    [Fact]
    public void Exactly_on_EffectiveFrom_the_entry_applies() => Assert.Equal(280.25m, On("2026-10-01"));

    [Fact]
    public void Between_entries_the_earlier_one_applies() => Assert.Equal(278.50m, On("2026-09-30"));

    [Fact]
    public void After_the_last_entry_it_still_applies() => Assert.Equal(282.00m, On("2027-03-01"));

    [Fact]
    public void A_future_dated_entry_is_ignored_until_its_date()
    {
        Assert.Equal(280.25m, On("2026-10-09")); // "today": the November entry is only scheduled
        Assert.Equal(280.25m, On("2026-10-31"));
        Assert.Equal(282.00m, On("2026-11-01"));
    }
}

public class ExchangeRateRulesTests
{
    [Theory]
    [InlineData("280.5", "280.50")]
    [InlineData("280", "280.00")]
    [InlineData("280.1234", "280.1234")]
    [InlineData("280.1200", "280.12")]
    [InlineData("280.1230", "280.123")]
    [InlineData("1000.0000", "1,000.00")]
    public void Format_trims_to_between_2_and_4_decimals(string value, string expected) =>
        Assert.Equal(expected, ExchangeRateRules.Format(decimal.Parse(value, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("280", "294", false)]       // exactly +5%
    [InlineData("280", "266", false)]       // exactly −5%
    [InlineData("280", "294.0001", true)]   // just over +5%
    [InlineData("280", "265.9999", true)]   // just over −5%
    [InlineData("280", "281.25", false)]
    [InlineData("280", "320", true)]
    [InlineData("280", "200", true)]
    public void Large_change_is_more_than_5_percent_either_way(string previous, string next, bool expected) =>
        Assert.Equal(expected, ExchangeRateRules.IsLargeChange(decimal.Parse(previous, CultureInfo.InvariantCulture), decimal.Parse(next, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("99.9999", false)]
    [InlineData("100", true)]
    [InlineData("1000", true)]
    [InlineData("1000.0001", false)]
    [InlineData("280.12345", false)]   // 5 decimals
    [InlineData("280.12340", true)]    // trailing zero is fine
    public void RateError_enforces_range_and_4_decimals(string value, bool valid) =>
        Assert.Equal(valid, ExchangeRateRules.RateError(decimal.Parse(value, CultureInfo.InvariantCulture)) is null);
}
