namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Provides consistent, predictable time values for integration tests.
/// Uses current time as base to ensure Nightscout returns entries (it has a time window filter).
/// Time is captured once at test collection start to maintain consistency within a test run.
/// </summary>
public static class TestTimeProvider
{
    /// <summary>
    /// Base time for consistent test behavior.
    /// Uses current UTC time (captured once) to ensure Nightscout returns entries
    /// in default queries, which filter out old data.
    /// </summary>
    public static readonly DateTimeOffset BaseTestTime = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets a test time with optional offset from base time
    /// </summary>
    /// <param name="minutesOffset">Minutes to offset from base time (can be negative)</param>
    /// <returns>Consistent test time</returns>
    public static DateTimeOffset GetTestTime(int minutesOffset = 0)
    {
        return BaseTestTime.AddMinutes(minutesOffset);
    }

    /// <summary>
    /// Gets a test time in Unix milliseconds
    /// </summary>
    /// <param name="minutesOffset">Minutes to offset from base time</param>
    /// <returns>Unix timestamp in milliseconds</returns>
    public static long GetTestTimeMillis(int minutesOffset = 0)
    {
        return GetTestTime(minutesOffset).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Gets a test time formatted as date string
    /// </summary>
    /// <param name="minutesOffset">Minutes to offset from base time</param>
    /// <param name="format">Date format string (defaults to ISO 8601)</param>
    /// <returns>Formatted date string</returns>
    public static string GetTestTimeString(
        int minutesOffset = 0,
        string format = "yyyy-MM-ddTHH:mm:ss.fffZ"
    )
    {
        return GetTestTime(minutesOffset).ToString(format);
    }

    /// <summary>
    /// Gets the test date only (without time) formatted as string
    /// </summary>
    /// <param name="daysOffset">Days to offset from base time</param>
    /// <returns>Date string in yyyy-MM-dd format</returns>
    public static string GetTestDateString(int daysOffset = 0)
    {
        return BaseTestTime.AddDays(daysOffset).ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Creates a range of test times for query testing
    /// </summary>
    /// <param name="startOffsetMinutes">Start time offset in minutes</param>
    /// <param name="endOffsetMinutes">End time offset in minutes</param>
    /// <returns>Tuple of start and end times</returns>
    public static (DateTimeOffset start, DateTimeOffset end) GetTestTimeRange(
        int startOffsetMinutes = -60,
        int endOffsetMinutes = 0
    )
    {
        return (GetTestTime(startOffsetMinutes), GetTestTime(endOffsetMinutes));
    }

    /// <summary>
    /// Creates a range of test times in Unix milliseconds for query testing
    /// </summary>
    /// <param name="startOffsetMinutes">Start time offset in minutes</param>
    /// <param name="endOffsetMinutes">End time offset in minutes</param>
    /// <returns>Tuple of start and end times in milliseconds</returns>
    public static (long start, long end) GetTestTimeRangeMillis(
        int startOffsetMinutes = -60,
        int endOffsetMinutes = 0
    )
    {
        return (GetTestTimeMillis(startOffsetMinutes), GetTestTimeMillis(endOffsetMinutes));
    }

    /// <summary>
    /// Generates a sequence of test times at regular intervals
    /// </summary>
    /// <param name="count">Number of timestamps to generate</param>
    /// <param name="intervalMinutes">Minutes between each timestamp</param>
    /// <param name="startOffset">Starting offset from base time</param>
    /// <returns>Array of sequential timestamps</returns>
    public static DateTimeOffset[] GenerateTimeSequence(
        int count = 5,
        int intervalMinutes = 5,
        int startOffset = 0
    )
    {
        var times = new DateTimeOffset[count];
        for (int i = 0; i < count; i++)
        {
            times[i] = GetTestTime(startOffset + (i * intervalMinutes));
        }
        return times;
    }

    /// <summary>
    /// Generates a sequence of test times in Unix milliseconds
    /// </summary>
    /// <param name="count">Number of timestamps to generate</param>
    /// <param name="intervalMinutes">Minutes between each timestamp</param>
    /// <param name="startOffset">Starting offset from base time</param>
    /// <returns>Array of sequential timestamps in milliseconds</returns>
    public static long[] GenerateTimeSequenceMillis(
        int count = 5,
        int intervalMinutes = 5,
        int startOffset = 0
    )
    {
        var times = new long[count];
        for (int i = 0; i < count; i++)
        {
            times[i] = GetTestTimeMillis(startOffset + (i * intervalMinutes));
        }
        return times;
    }
}
