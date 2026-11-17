namespace Nocturne.Core.Models.V4;

/// <summary>
/// CGM-specific properties for a <see cref="DeviceCatalogEntry"/>, describing sensor lifetime
/// and transmission characteristics.
/// </summary>
/// <seealso cref="DeviceCatalogEntry"/>
/// <seealso cref="DeviceCatalog"/>
public record CgmProperties
{
    /// <summary>
    /// Maximum approved wear duration for a single sensor in days (e.g., 10 for Dexcom G7, 14 for Libre 3).
    /// </summary>
    public required int SensorDurationDays { get; init; }

    /// <summary>Hours past <see cref="SensorDurationDays"/> the sensor keeps working before it stops; null when the manufacturer states none.</summary>
    public int? GraceHours { get; init; }

    /// <summary>
    /// Warm-up period in minutes before the CGM begins reporting readings after sensor insertion.
    /// </summary>
    public required int WarmupMinutes { get; init; }

    /// <summary>
    /// How often the CGM transmits a new glucose reading, in minutes (e.g., 5 for Dexcom, 1 for Libre 3).
    /// </summary>
    public required int UpdateIntervalMinutes { get; init; }

    /// <summary>
    /// Whether this CGM model uses a separate, reusable transmitter in addition to the disposable sensor.
    /// </summary>
    public required bool HasSeparateTransmitter { get; init; }

    /// <summary>
    /// Maximum lifetime of the separate transmitter in days (null when <see cref="HasSeparateTransmitter"/> is false).
    /// </summary>
    public int? TransmitterDurationDays { get; init; }
}
