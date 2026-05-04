namespace Nocturne.Core.Models.V4;

/// <summary>
/// A diabetes management device associated with a patient, including its usage period and AID algorithm.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PatientDevice"/> records the patient's ownership or use of a specific physical device
/// (pump, CGM, meter, pen) during a date range. When the device is an AID-capable pump,
/// <see cref="AidAlgorithm"/> identifies the control algorithm in use. <see cref="CatalogId"/>
/// links to a known model in <see cref="DeviceCatalog"/>; <see cref="DeviceId"/> links to the
/// auto-discovered <see cref="Device"/> record from upload data, when available.
/// </para>
/// <para>
/// <b><see cref="PatientDevice"/> vs. <see cref="Device"/>:</b> <see cref="PatientDevice"/> is
/// <i>declaration</i> -- patient-curated metadata that can exist before any data arrives
/// (e.g., "I'll start using an Omnipod 5 on May 1") and carries fields the wire format never
/// supplies: usage window, AID algorithm, manufacturer/model, free-text notes.
/// <see cref="Device"/> is <i>observation</i> -- a row upserted by the ingest pipeline for every
/// <c>(category, type, serial)</c> triple seen in uploads, regardless of who owns it. Time-series
/// records (<see cref="Bolus"/>, <see cref="TempBasal"/>, <see cref="PumpSnapshot"/>, etc.) FK to
/// <see cref="Device"/>, not <see cref="PatientDevice"/>. The split lets a patient have multiple
/// sequential <see cref="PatientDevice"/> rows over years (one per pump they've used) without
/// losing the per-upload <see cref="Device"/> trail, and lets ingestion proceed before the
/// patient has curated anything.
/// </para>
/// </remarks>
/// <seealso cref="Device"/>
/// <seealso cref="DeviceCategory"/>
/// <seealso cref="AidAlgorithm"/>
/// <seealso cref="PatientRecord"/>
/// <seealso cref="DeviceCatalogEntry"/>
public class PatientDevice
{
    /// <summary>
    /// UUID v7 primary key.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Category of this device (pump, CGM, meter, pen, etc.).
    /// </summary>
    /// <seealso cref="V4.DeviceCategory"/>
    public DeviceCategory DeviceCategory { get; set; }

    /// <summary>
    /// Device manufacturer name (e.g., "Insulet", "Dexcom", "Abbott").
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Device model name (e.g., "Omnipod 5", "Dexcom G7").
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Identifier in the <see cref="DeviceCatalog"/> (e.g., "omnipod-5", "dexcom-g7").
    /// Null for custom or uncatalogued devices.
    /// </summary>
    public string? CatalogId { get; set; }

    /// <summary>
    /// AID algorithm running on this device, if applicable.
    /// </summary>
    /// <seealso cref="V4.AidAlgorithm"/>
    public AidAlgorithm? AidAlgorithm { get; set; }

    /// <summary>
    /// Physical serial number of the device, if known.
    /// </summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// Foreign key to the auto-discovered <see cref="Device"/> record (null if not yet matched).
    /// </summary>
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Date the patient started using this device (inclusive).
    /// </summary>
    public DateOnly? StartDate { get; set; }

    /// <summary>
    /// Date the patient stopped using this device (inclusive), or null if still in use.
    /// </summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>
    /// Whether this is the patient's currently active device of this category.
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// Optional free-text notes about this device (e.g., reason for switch, warranty info).
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When this record was first created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified (UTC).
    /// </summary>
    public DateTime ModifiedAt { get; set; }
}
