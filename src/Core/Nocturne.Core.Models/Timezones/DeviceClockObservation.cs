namespace Nocturne.Core.Models.Timezones;

/// <summary>
/// Where a <see cref="DeviceClockObservation"/> came from.
/// </summary>
public enum DeviceClockObservationSource
{
    /// <summary>
    /// The account's own profile record — the connector's phone app writes the device's current UTC
    /// offset to the vendor account whenever it drifts, so each distinct profile update asserts the
    /// device offset at the moment the record was written.
    /// </summary>
    Profile = 0,

    /// <summary>
    /// Derived from an upload batch: records share a server-side real-UTC upload timestamp while
    /// their clinical timestamps carry the device's wall clock, so the difference bounds (or, for a
    /// dense prompt upload, estimates) the device's effective UTC offset.
    /// </summary>
    UploadBatch = 1,
}

/// <summary>
/// One piece of evidence about a device's effective UTC offset at a moment in real UTC time.
/// Observations are stored separately from the user-asserted timezone timeline so derived knowledge
/// can never clobber a manual correction, re-derivation stays idempotent, and a wrong timestamp can
/// always be traced back to the evidence that produced it.
/// </summary>
/// <seealso cref="DeviceClockSegmenter"/>
public sealed class DeviceClockObservation
{
    /// <summary>Connector the evidence came from (e.g. "glooko"). A derived correction must stay scoped to the connector that produced it.</summary>
    public string Connector { get; set; } = string.Empty;

    public DeviceClockObservationSource Source { get; set; }

    /// <summary>
    /// Real-UTC instant the offset was observed: the profile record's own update time, or the
    /// upload batch's sync timestamp.
    /// </summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>Minutes east of UTC the device clock showed.</summary>
    public int OffsetMinutes { get; set; }

    /// <summary>
    /// True when <see cref="OffsetMinutes"/> is a two-sided estimate (profile assertion, or a dense
    /// prompt upload); false when it is only a hard lower bound (offset minus an unknown non-negative
    /// upload lag). Lower bounds can prove the clock ran ahead of the expected zone but can never
    /// refute a deviation — westward travel is invisible to them.
    /// </summary>
    public bool IsEstimate { get; set; }

    /// <summary>Number of records that produced this observation (1 for profile assertions).</summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Real-UTC instant of the oldest clinical record in the batch, converted with this observation's
    /// own offset — the earliest data this observation is evidence about. Null for profile assertions.
    /// </summary>
    public DateTime? CoversFromUtc { get; set; }

    /// <summary>The IANA zone the account declared at observation time. Profile source only.</summary>
    public string? DeclaredTimezone { get; set; }
}
