using HR.Domain.Payroll;

namespace HR.Tests.Unit;

public class PayPeriodTests
{
    // SPEC §9 "Period helper cases".
    [Theory]
    [InlineData("2026-10-08", "2026-10-01", "2026-10-15")]
    [InlineData("2026-10-16", "2026-10-16", "2026-10-31")]
    [InlineData("2028-02-20", "2028-02-16", "2028-02-29")]
    [InlineData("2027-02-16", "2027-02-16", "2027-02-28")]
    [InlineData("2026-12-31", "2026-12-16", "2026-12-31")]
    public void For_returns_the_spec_period(string date, string expectedStart, string expectedEnd)
    {
        var period = PayPeriod.For(DateOnly.Parse(date, CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.Parse(expectedStart, CultureInfo.InvariantCulture), period.Start);
        Assert.Equal(DateOnly.Parse(expectedEnd, CultureInfo.InvariantCulture), period.End);
    }

    [Fact]
    public void Feb_16_to_29_2028_has_10_working_days()
    {
        Assert.Equal(10, PayPeriod.For(new DateOnly(2028, 2, 20)).WorkingDayCount);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-01", "2026-10-15")] // first day of a month
    [InlineData("2026-10-15", "2026-10-01", "2026-10-15")] // last day of the first half
    [InlineData("2026-10-31", "2026-10-16", "2026-10-31")] // 31-day month end
    [InlineData("2026-11-30", "2026-11-16", "2026-11-30")] // 30-day month end
    [InlineData("2026-02-28", "2026-02-16", "2026-02-28")] // non-leap February
    public void For_handles_boundaries(string date, string expectedStart, string expectedEnd)
    {
        var period = PayPeriod.For(DateOnly.Parse(date, CultureInfo.InvariantCulture));

        Assert.Equal(DateOnly.Parse(expectedStart, CultureInfo.InvariantCulture), period.Start);
        Assert.Equal(DateOnly.Parse(expectedEnd, CultureInfo.InvariantCulture), period.End);
    }

    [Fact]
    public void First_half_and_second_half_are_flagged()
    {
        Assert.True(PayPeriod.For(new DateOnly(2026, 10, 8)).IsFirstHalf);
        Assert.False(PayPeriod.For(new DateOnly(2026, 10, 16)).IsFirstHalf);
    }

    [Fact]
    public void Next_moves_to_the_second_half_then_to_the_next_month_and_year()
    {
        var oct1 = PayPeriod.For(new DateOnly(2026, 10, 8));
        var oct16 = oct1.Next();
        Assert.Equal(new DateOnly(2026, 10, 16), oct16.Start);
        Assert.Equal(new DateOnly(2026, 10, 31), oct16.End);

        var nov1 = oct16.Next();
        Assert.Equal(new DateOnly(2026, 11, 1), nov1.Start);
        Assert.Equal(new DateOnly(2026, 11, 15), nov1.End);

        var jan1 = PayPeriod.For(new DateOnly(2026, 12, 31)).Next();
        Assert.Equal(new DateOnly(2027, 1, 1), jan1.Start);
        Assert.Equal(new DateOnly(2027, 1, 15), jan1.End);
    }

    [Fact]
    public void Previous_moves_back_across_a_year_boundary()
    {
        var previous = PayPeriod.For(new DateOnly(2027, 1, 5)).Previous();

        Assert.Equal(new DateOnly(2026, 12, 16), previous.Start);
        Assert.Equal(new DateOnly(2026, 12, 31), previous.End);
    }

    [Fact]
    public void Periods_are_value_equal_and_contain_their_dates()
    {
        var a = PayPeriod.For(new DateOnly(2026, 10, 3));
        var b = PayPeriod.For(new DateOnly(2026, 10, 12));

        Assert.Equal(a, b);
        Assert.True(a.Contains(new DateOnly(2026, 10, 1)));
        Assert.True(a.Contains(new DateOnly(2026, 10, 15)));
        Assert.False(a.Contains(new DateOnly(2026, 10, 16)));
        Assert.False(a.Contains(new DateOnly(2026, 9, 30)));
    }
}
