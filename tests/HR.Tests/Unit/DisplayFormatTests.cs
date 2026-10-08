using HR.Web.Formatting;

namespace HR.Tests.Unit;

public class DisplayFormatTests
{
    [Theory]
    [InlineData("1234.56", "$1,234.56")]
    [InlineData("150", "$150.00")]
    [InlineData("0", "$0.00")]
    [InlineData("-13.64", "-$13.64")]
    public void Usd(string amount, string expected) => Assert.Equal(expected, DisplayFormat.Usd(decimal.Parse(amount, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1234567", "Rs 1,234,567")]
    [InlineData("42000", "Rs 42,000")]
    [InlineData("0", "Rs 0")]
    [InlineData("-3819", "-Rs 3,819")]
    public void Pkr(string amount, string expected) => Assert.Equal(expected, DisplayFormat.Pkr(decimal.Parse(amount, CultureInfo.InvariantCulture)));

    [Fact]
    public void Date_uses_dd_MMM_yyyy() => Assert.Equal("08 Oct 2026", DisplayFormat.Date(new DateOnly(2026, 10, 8)));

    [Fact]
    public void DateRange_is_compact_within_a_month() =>
        Assert.Equal("01–15 Oct 2026", DisplayFormat.DateRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15)));

    [Fact]
    public void DateRange_spells_out_both_dates_across_months() =>
        Assert.Equal("16 Dec 2026 – 01 Jan 2027", DisplayFormat.DateRange(new DateOnly(2026, 12, 16), new DateOnly(2027, 1, 1)));
}
