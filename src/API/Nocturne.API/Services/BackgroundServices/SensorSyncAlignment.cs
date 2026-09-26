namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// When to next ask a sensor's cloud for data, given the newest reading already stored and how often
/// the sensor produces one. The reading reaches the cloud some time after its sensor timestamp, so a
/// poll on a free-running timer lands at an arbitrary phase of the sensor's cadence and can trail each
/// reading by almost a full interval. Aligning to the reading instead (as nightscout-connect's
/// <c>align_to_glucose</c> does) asks just after the next one should exist, and retries briefly when
/// it is late.
/// </summary>
/// <remarks>
/// Every allowance is bounded by the cadence as well as by its own ceiling, so a one-minute sensor is
/// not retried more often than it produces readings; at a five-minute cadence the ceilings apply.
/// </remarks>
internal static class SensorSyncAlignment
{
    /// <summary>Allowance for a reading to reach the cloud after its sensor timestamp.</summary>
    public static readonly TimeSpan PublishBuffer = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Ceiling on the random spread added to an on-time poll, so many tenants aligned to readings
    /// taken at the same moment do not reach the cloud in the same second.
    /// </summary>
    public static readonly TimeSpan MaxJitter = TimeSpan.FromSeconds(15);

    /// <summary>Ceiling on how soon to ask again while the expected reading is late.</summary>
    public static readonly TimeSpan LateRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling on how long past its due time a reading is still treated as merely delayed in the
    /// cloud. Beyond it the gap is a real one (signal loss, warm-up, sensor change) and polling
    /// returns to the reading grid rather than retrying.
    /// </summary>
    public static readonly TimeSpan LateRetryWindow = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Past this age the newest reading says nothing useful about when the next one will arrive (the
    /// sensor is off or expired), and the connector's own interval is left in charge.
    /// </summary>
    public static readonly TimeSpan MaxAlignableAge = TimeSpan.FromHours(1);

    /// <summary>The jitter bound for a sensor reading every <paramref name="cadence"/>.</summary>
    public static TimeSpan JitterFor(TimeSpan cadence) => Shorter(MaxJitter, cadence / 10);

    /// <summary>The late-reading retry spacing for a sensor reading every <paramref name="cadence"/>.</summary>
    public static TimeSpan LateRetryIntervalFor(TimeSpan cadence) => Shorter(LateRetryInterval, cadence / 2);

    /// <summary>The late-reading retry window for a sensor reading every <paramref name="cadence"/>.</summary>
    public static TimeSpan LateRetryWindowFor(TimeSpan cadence) => Shorter(LateRetryWindow, cadence * 0.6);

    /// <summary>
    /// The time to next poll, or <c>null</c> when <paramref name="latestReading"/> is too old to
    /// align to.
    /// </summary>
    /// <param name="latestReading">UTC sensor timestamp of the newest stored reading.</param>
    /// <param name="cadence">How often the sensor produces a reading.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="jitterFraction">A value in [0, 1] choosing where in the jitter bound to land.</param>
    public static DateTime? NextSyncAt(DateTime latestReading, TimeSpan cadence, DateTime now, double jitterFraction)
    {
        if (cadence <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "A sensor cadence must be positive.");

        var age = now - latestReading;
        if (age > MaxAlignableAge)
            return null;

        var jitter = JitterFor(cadence) * Math.Clamp(jitterFraction, 0d, 1d);
        var expected = latestReading + cadence;

        // The next reading is not due yet: ask just after it should have reached the cloud.
        if (now < expected + PublishBuffer)
            return expected + PublishBuffer + jitter;

        // Due but not in the cloud yet, which is usually the uploading phone being slow. Keep asking.
        if (now - expected < LateRetryWindowFor(cadence))
            return now + LateRetryIntervalFor(cadence);

        // A real gap: go back to the reading grid, one slot at a time, instead of hammering.
        var slotsBehind = Math.Ceiling(age / cadence);
        var nextSlot = latestReading + cadence * slotsBehind;
        if (nextSlot + PublishBuffer <= now)
            nextSlot += cadence;

        return nextSlot + PublishBuffer + jitter;
    }

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
