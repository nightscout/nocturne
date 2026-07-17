namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting an uploader/phone status snapshot via the V4 API.
/// Mirrors <see cref="Nocturne.Core.Models.V4.UploaderSnapshot"/> minus the server-assigned
/// fields (Id, CreatedAt, ModifiedAt, LegacyId, Mills, device FK).
/// </summary>
/// <remarks>
/// Records carrying both <see cref="DataSource"/> and <see cref="SyncIdentifier"/> are upserted:
/// a row already matched by that pair is updated in place, so uploader retries of the same loop
/// cycle stay idempotent. <see cref="CorrelationId"/> ties one loop cycle's APS, pump, and
/// uploader snapshots together.
/// </remarks>
/// <seealso cref="Nocturne.API.Controllers.V4.Devices.UploaderSnapshotController"/>
public class UpsertUploaderSnapshotRequest
{
    /// <summary>
    /// When the uploader status was read.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the uploader device.
    /// </summary>
    public string? Device { get; set; }

    /// <summary>
    /// Name of the application that submitted this record.
    /// </summary>
    public string? App { get; set; }

    /// <summary>
    /// Upstream data source identifier; required when SyncIdentifier is supplied.
    /// </summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier. When paired with DataSource, re-uploading the same
    /// snapshot updates the existing record in place rather than creating a duplicate.
    /// </summary>
    public string? SyncIdentifier { get; set; }

    /// <summary>
    /// Correlation identifier shared by the APS, pump, and uploader snapshots of one loop cycle.
    /// </summary>
    public Guid? CorrelationId { get; set; }

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

    /// <summary>Catch-all for fields not mapped to dedicated columns</summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
