using HR.Domain.Time;

namespace HR.Infrastructure.Time;

/// <summary>The production clock: system UTC time, with "today" in Asia/Karachi.</summary>
public sealed class SystemClock(TimeProvider timeProvider) : IClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public DateOnly Today => PakistanTime.ToKarachiDate(UtcNow);
}
