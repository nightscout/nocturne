namespace Nocturne.Core.Models.Timezones;

/// <summary>
/// A span of real UTC time during which a connector's device clock ran at a fixed UTC offset that
/// deviates from the tenant's timezone timeline. Derived from <see cref="DeviceClockObservation"/>
/// evidence by <see cref="DeviceClockSegmenter"/>; never persisted into the timeline itself. The
/// offset is fixed (no DST rules) because it tracks the device's clock, not the person's location.
/// </summary>
public sealed class DeviceClockSegment
{
    /// <summary>Real-UTC start of the deviation window.</summary>
    public DateTime FromUtc { get; set; }

    /// <summary>Real-UTC end of the deviation window; null while the deviation is ongoing.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>Minutes east of UTC the device clock ran at during this window.</summary>
    public int OffsetMinutes { get; set; }

    /// <summary>How many observations support this segment.</summary>
    public int ObservationCount { get; set; }

    /// <summary>Whether a real-UTC instant falls inside this segment.</summary>
    public bool Contains(DateTime utc) => utc >= FromUtc && (ToUtc is null || utc < ToUtc);
}
