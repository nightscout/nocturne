namespace Nocturne.Core.Models.V4;

/// <summary>
/// The <c>glucose_reading</c> message the Home Assistant hub sends for a live sensor glucose write.
/// </summary>
public record GlucoseReadingEvent
{
    /// <summary>
    /// The readings the write created on the tenant's canonical CGM stream, oldest first, so the last
    /// is the newest.
    /// </summary>
    public required IReadOnlyList<SensorGlucose> Readings { get; init; }
}
