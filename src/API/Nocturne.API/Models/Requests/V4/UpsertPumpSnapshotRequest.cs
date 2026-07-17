namespace Nocturne.API.Models.Requests.V4;

/// <summary>
/// Request body for upserting a pump status snapshot via the V4 API.
/// Mirrors <see cref="Nocturne.Core.Models.V4.PumpSnapshot"/> minus the server-assigned fields
/// (Id, CreatedAt, ModifiedAt, LegacyId, Mills, device FKs).
/// </summary>
/// <remarks>
/// Records carrying both <see cref="DataSource"/> and <see cref="SyncIdentifier"/> are upserted:
/// a row already matched by that pair is updated in place, so uploader retries of the same loop
/// cycle stay idempotent. <see cref="CorrelationId"/> ties one loop cycle's APS, pump, and
/// uploader snapshots together. Omit <see cref="Reservoir"/> when the level is unknown rather
/// than sending a sentinel value.
/// </remarks>
/// <seealso cref="Nocturne.API.Controllers.V4.Devices.PumpSnapshotController"/>
public class UpsertPumpSnapshotRequest
{
    /// <summary>
    /// When the pump status was read.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes at the time of the event, for local-time display.
    /// </summary>
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Identifier of the device that produced this snapshot (e.g. pump serial number).
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
    /// Pump manufacturer name (e.g., "Insulet", "Medtronic", "Tandem").
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Pump model name (e.g., "Omnipod DASH", "MiniMed 780G").
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Insulin remaining in the reservoir (units). Omit when unknown.
    /// </summary>
    public double? Reservoir { get; set; }

    /// <summary>
    /// Human-readable reservoir display string (e.g., "50+ U", "Low").
    /// </summary>
    public string? ReservoirDisplay { get; set; }

    /// <summary>
    /// Pump battery level as a percentage (0-100).
    /// </summary>
    public int? BatteryPercent { get; set; }

    /// <summary>
    /// Pump battery voltage (for devices that report voltage instead of percentage).
    /// </summary>
    public double? BatteryVoltage { get; set; }

    /// <summary>
    /// Whether the pump is currently delivering a bolus.
    /// </summary>
    public bool? Bolusing { get; set; }

    /// <summary>
    /// Whether the pump is currently in a suspended state.
    /// </summary>
    public bool? Suspended { get; set; }

    /// <summary>
    /// Pump status string as reported by the device (e.g., "normal", "suspended", "bolusing").
    /// </summary>
    public string? PumpStatus { get; set; }

    /// <summary>
    /// Canonical closed-loop operating mode (a PumpModeState name such as "Automatic" or
    /// "Manual"), when the source reports it.
    /// </summary>
    public string? PumpMode { get; set; }

    /// <summary>
    /// Pump internal clock time as a string (device-local time).
    /// </summary>
    public string? Clock { get; set; }

    /// <summary>Pump-reported total IOB (when no APS algorithm is running)</summary>
    public double? Iob { get; set; }

    /// <summary>Pump-reported bolus IOB</summary>
    public double? BolusIob { get; set; }

    /// <summary>Catch-all for fields not mapped to dedicated columns (e.g. bolus increment)</summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
