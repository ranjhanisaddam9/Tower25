using HR.Domain.Time;
using HR.Infrastructure.Time;

namespace HR.Tests.Unit;

public class ClockTests
{
    [Theory]
    [InlineData("2026-10-08T18:59:59Z", "2026-10-08")] // 23:59:59 in Karachi
    [InlineData("2026-10-08T19:00:00Z", "2026-10-09")] // midnight in Karachi (UTC+5)
    [InlineData("2026-12-31T19:30:00Z", "2027-01-01")] // new year arrives first in Karachi
    public void Today_is_the_Asia_Karachi_calendar_date(string utc, string expectedDate)
    {
        var clock = new SystemClock(new FixedTimeProvider(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(DateOnly.Parse(expectedDate, CultureInfo.InvariantCulture), clock.Today);
    }

    [Fact]
    public void UtcNow_comes_from_the_time_provider()
    {
        var instant = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(instant, new SystemClock(new FixedTimeProvider(instant)).UtcNow);
    }

    [Fact]
    public void Karachi_zone_is_utc_plus_five()
    {
        Assert.Equal(TimeSpan.FromHours(5), PakistanTime.Zone.GetUtcOffset(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
