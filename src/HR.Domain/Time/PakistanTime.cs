namespace HR.Domain.Time;

/// <summary>
/// Converts instants to Pakistan calendar dates. Pure: the caller supplies the instant.
/// </summary>
public static class PakistanTime
{
    /// <summary>Pakistan Standard Time (UTC+05:00, no daylight saving).</summary>
    public static TimeZoneInfo Zone { get; } = ResolveZone();

    public static DateOnly ToKarachiDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);

    private static TimeZoneInfo ResolveZone()
    {
        // IANA id works on Linux and on Windows with ICU; the Windows id is the fallback.
        foreach (var id in new[] { "Asia/Karachi", "Pakistan Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Asia/Karachi", TimeSpan.FromHours(5), "Pakistan Standard Time", "PKT");
    }
}
