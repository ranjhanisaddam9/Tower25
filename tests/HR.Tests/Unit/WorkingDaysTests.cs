using HR.Domain.Payroll;

namespace HR.Tests.Unit;

public class WorkingDaysTests
{
    [Theory]
    [InlineData("2026-10-01", "2026-10-15", 11)]
    [InlineData("2026-10-16", "2026-10-31", 11)]
    [InlineData("2028-02-16", "2028-02-29", 10)]
    public void Count_matches_the_milestone_cases(string start, string end, int expected)
    {
        Assert.Equal(expected, WorkingDays.Count(DateOnly.Parse(start, CultureInfo.InvariantCulture), DateOnly.Parse(end, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("2026-10-03", "2026-10-04", 0)] // Saturday–Sunday
    [InlineData("2026-10-05", "2026-10-05", 1)] // a single Monday
    [InlineData("2026-10-10", "2026-10-10", 0)] // a single Saturday
    [InlineData("2026-10-05", "2026-10-11", 5)] // one full week
    [InlineData("2026-10-08", "2026-10-15", 6)] // G2: joined Thu Oct 8
    [InlineData("2026-10-16", "2026-10-21", 4)] // G7: leaving Wed Oct 21
    public void Count_includes_both_ends_and_skips_weekends(string start, string end, int expected)
    {
        Assert.Equal(expected, WorkingDays.Count(DateOnly.Parse(start, CultureInfo.InvariantCulture), DateOnly.Parse(end, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Count_is_zero_for_an_empty_range()
    {
        Assert.Equal(0, WorkingDays.Count(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 1)));
    }

    [Theory]
    [InlineData("2026-10-03", false)] // Saturday
    [InlineData("2026-10-04", false)] // Sunday
    [InlineData("2026-10-05", true)]  // Monday
    [InlineData("2026-10-09", true)]  // Friday
    public void IsWorkingDay_is_monday_to_friday(string date, bool expected)
    {
        Assert.Equal(expected, WorkingDays.IsWorkingDay(DateOnly.Parse(date, CultureInfo.InvariantCulture)));
    }
}
