namespace Nocturne.Core.Models.V4;

/// <summary>
/// Device event record (site change, sensor start, pump battery change, etc.).
/// </summary>
/// <remarks>
/// This is the V4 equivalent of legacy <see cref="Treatment"/> records whose event type
/// represents a device lifecycle action (e.g., "Site Change", "Sensor Start", "Pump Battery Change").
/// The <see cref="EventType"/> is a strongly-typed <see cref="DeviceEventType"/> enum rather than
/// a freeform string.
/// </remarks>
/// <seealso cref="Treatment"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="DeviceEventType"/>
/// <seealso cref="Device"/>
/// <seealso cref="Note"/>
public class DeviceEvent : IV4Record
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Canonical timestamp as UTC DateTime
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Unix milliseconds (computed from Timestamp for v1/v3 compatibility)
    /// </summary>
    public long Mills => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that created this record
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="Device"/> table.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> table.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Application that uploaded this record
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    public string? LegacyId { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified
    /// </summary>
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Type of device event (e.g. <see cref="DeviceEventType.SiteChange"/>,
    /// <see cref="DeviceEventType.SensorStart"/>).
    /// </summary>
    public DeviceEventType EventType { get; set; }

    /// <summary>
    /// Free-text notes about the device event
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// APS system sync/deduplication identifier (used by Loop and AAPS)
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
