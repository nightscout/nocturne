namespace Nocturne.Core.Models.V4;

/// <summary>
/// Normalized uploader/phone status snapshot extracted from a legacy <see cref="DeviceStatus"/> record.
/// Fully typed -- no JSONB blobs needed.
/// </summary>
/// <remarks>
/// A single legacy <see cref="DeviceStatus"/> is decomposed into up to three V4 records:
/// an <see cref="ApsSnapshot"/>, a <see cref="PumpSnapshot"/>, and an <see cref="UploaderSnapshot"/>,
/// all sharing the same <see cref="IV4Record.CorrelationId"/>.
/// </remarks>
/// <seealso cref="DeviceStatus"/>
/// <seealso cref="IV4Record"/>
/// <seealso cref="ApsSnapshot"/>
/// <seealso cref="PumpSnapshot"/>
/// <seealso cref="Device"/>
public class UploaderSnapshot : IV4Record
{
    /// <inheritdoc />
    public Guid Id { get; set; }

    /// <inheritdoc />
    public DateTime Timestamp { get; set; }

    /// <inheritdoc />
    public long Mills => new DateTimeOffset(Timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <inheritdoc />
    public int? UtcOffset { get; set; }

    /// <inheritdoc />
    public string? Device { get; set; }

    /// <inheritdoc />
    public string? App { get; set; }

    /// <inheritdoc />
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier. Records matched on (DataSource, SyncIdentifier) are updated in
    /// place on re-upload instead of duplicated, so uploader retries are idempotent.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <inheritdoc />
    public Guid? CorrelationId { get; set; }

    /// <inheritdoc />
    public string? LegacyId { get; set; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Uploader device name (e.g., phone model or bridge device name).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Uploader battery level as a percentage (0-100).
    /// </summary>
    public int? Battery { get; set; }

    /// <summary>
    /// Uploader battery voltage (for devices that report voltage).
    /// </summary>
    public double? BatteryVoltage { get; set; }

    /// <summary>
    /// Whether the uploader device is currently charging.
    /// </summary>
    public bool? IsCharging { get; set; }

    /// <summary>
    /// Uploader device temperature in degrees Celsius.
    /// </summary>
    public double? Temperature { get; set; }

    /// <summary>
    /// Uploader device type identifier (e.g., "phone", "bridge").
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="V4.Device"/> table.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
