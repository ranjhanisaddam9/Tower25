namespace HR.Domain.Time;

/// <summary>
/// The single source of "now" and "today". Business code never calls DateTime.Now.
/// </summary>
public interface IClock
{
    /// <summary>The current instant, in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Today's calendar date in Asia/Karachi (SPEC §3).</summary>
    DateOnly Today { get; }
}
