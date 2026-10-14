namespace Nocturne.Core.Models.V4;

/// <summary>
/// A known device model entry in the <see cref="DeviceCatalog"/>, containing manufacturer metadata
/// and optional <see cref="CgmProperties"/> for CGM devices.
/// </summary>
/// <seealso cref="DeviceCatalog"/>
/// <seealso cref="DeviceCategory"/>
/// <seealso cref="CgmProperties"/>
/// <seealso cref="PatientDevice"/>
public record DeviceCatalogEntry
{
    /// <summary>
    /// Unique kebab-case identifier for this device model (e.g., "omnipod-5", "dexcom-g7").
    /// Used as the <see cref="PatientDevice.CatalogId"/> reference.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable display name of the device model (e.g., "Omnipod 5", "Dexcom G7").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Manufacturer name (e.g., "Insulet", "Dexcom", "Abbott").
    /// </summary>
    public required string Manufacturer { get; init; }

    /// <summary>
    /// Device category discriminator (pump, CGM, meter, etc.).
    /// </summary>
    /// <seealso cref="DeviceCategory"/>
    public required DeviceCategory Category { get; init; }

    /// <summary>
    /// CGM-specific properties (sensor duration, warm-up, transmitter info).
    /// Null for non-CGM devices.
    /// </summary>
    public CgmProperties? Cgm { get; init; }

    /// <summary>
    /// Insulin-pump properties (rated pod, infusion-set and reservoir wear times).
    /// Null for non-pump devices and for a custom pump.
    /// </summary>
    public PumpProperties? Pump { get; init; }
}
