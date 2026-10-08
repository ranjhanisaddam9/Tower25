using HR.Domain.Payroll;

namespace HR.Tests.Unit;

public class MoneyTests
{
    [Theory]
    [InlineData("0.125", "0.13")]     // .5 midpoint rounds away from zero (banker's rounding would give 0.12)
    [InlineData("0.135", "0.14")]
    [InlineData("2.345", "2.35")]
    [InlineData("-0.125", "-0.13")]   // away from zero, not up
    [InlineData("95.455", "95.46")]
    [InlineData("1.124", "1.12")]
    [InlineData("150", "150.00")]
    public void RoundUsd_rounds_to_two_decimals_away_from_zero(string input, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Money.RoundUsd(decimal.Parse(input, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("0.5", "1")]          // banker's rounding would give 0
    [InlineData("2.5", "3")]          // banker's rounding would give 2
    [InlineData("22909.5", "22910")]
    [InlineData("-2.5", "-3")]
    [InlineData("41999.49", "41999")]
    [InlineData("42000", "42000")]
    public void RoundPkr_rounds_to_whole_rupees_away_from_zero(string input, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Money.RoundPkr(decimal.Parse(input, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void RoundUsd_keeps_two_decimal_scale()
    {
        Assert.Equal("150.00", Money.RoundUsd(150.004m).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
