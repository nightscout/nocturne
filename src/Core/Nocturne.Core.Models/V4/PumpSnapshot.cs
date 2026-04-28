namespace Nocturne.Core.Models.V4;

/// <summary>
/// Normalized pump status snapshot extracted from a legacy <see cref="DeviceStatus"/> record.
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
/// <seealso cref="UploaderSnapshot"/>
/// <seealso cref="Device"/>
public class PumpSnapshot : IV4Record
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

    /// <inheritdoc />
    public Guid? CorrelationId { get; set; }

    /// <inheritdoc />
    public string? LegacyId { get; set; }

    /// <inheritdoc />
    public DateTime CreatedAt { get; set; }

    /// <inheritdoc />
    public DateTime ModifiedAt { get; set; }

    /// <summary>
    /// Pump manufacturer name (e.g., "Insulet", "Medtronic", "Tandem").
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Pump model name (e.g., "Omnipod DASH", "MiniMed 780G").
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Insulin remaining in the reservoir (units).
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
    /// Pump status string as reported by the device (e.g., "normal", "suspended").
    /// </summary>
    public string? PumpStatus { get; set; }

    /// <summary>
    /// Pump internal clock time as a string (device-local time).
    /// </summary>
    public string? Clock { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="V4.Device"/> table.
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the <see cref="PatientDevice"/> table.
    /// </summary>
    public Guid? PatientDeviceId { get; set; }

    /// <summary>Pump-reported total IOB (when no APS algorithm is running).</summary>
    public double? Iob { get; set; }

    /// <summary>Pump-reported bolus IOB.</summary>
    public double? BolusIob { get; set; }

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
