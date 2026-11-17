namespace Nocturne.Core.Models.V4;

/// <summary>
/// The <c>glucose_reading</c> message the Home Assistant hub sends for a live sensor glucose write.
/// </summary>
/// <remarks>
/// Carries new readings only: those a live write created on the tenant's canonical CGM stream that
/// are newer than every reading the tenant already held. A restored, gap-filled or late reading is
/// not sent, and neither is an update or correction of a stored reading.
/// </remarks>
public record GlucoseReadingEvent
{
    /// <summary>The new readings, oldest first, so the last is the tenant's current reading.</summary>
    public required IReadOnlyList<SensorGlucose> Readings { get; init; }
}
