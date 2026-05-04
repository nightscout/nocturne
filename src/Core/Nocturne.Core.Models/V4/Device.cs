namespace Nocturne.Core.Models.V4;

/// <summary>
/// Represents a physical device identified by category, type, and serial number,
/// auto-discovered from uploaded data.
/// </summary>
/// <remarks>
/// <para>
/// Devices are upserted by the ingest pipeline whenever an upload arrives with a new
/// <c>(<see cref="Category"/>, <see cref="Type"/>, <see cref="Serial"/>)</c> triple. Time-series
/// records (<see cref="Bolus"/>, <see cref="TempBasal"/>, <see cref="PumpSnapshot"/>,
/// <see cref="UploaderSnapshot"/>, <see cref="ApsSnapshot"/>, <see cref="DeviceEvent"/>,
/// <see cref="MeterGlucose"/>) reference a device via a <c>DeviceId</c> foreign key.
/// </para>
/// <para>
/// <b><see cref="Device"/> vs. <see cref="PatientDevice"/>:</b> <see cref="Device"/> is
/// <i>observation</i> -- "this serial number was seen in an upload" -- and exists independently
/// of any user action. <see cref="PatientDevice"/> is <i>declaration</i> -- the patient's
/// curated record of which device they use, with usage window (<see cref="PatientDevice.StartDate"/>
/// / <see cref="PatientDevice.EndDate"/>), <see cref="AidAlgorithm"/>, catalog linkage, and
/// notes. A <see cref="PatientDevice"/> can exist before any data arrives (declared intent), and
/// a <see cref="Device"/> can exist before the patient has curated it (raw observation). The two
/// are linked once matched, via <see cref="PatientDevice.DeviceId"/>. Splitting them keeps
/// observation and intent on independent lifecycles and supports multiple sequential
/// <see cref="PatientDevice"/> rows over time as patients switch hardware.
/// </para>
/// <para>
/// <see cref="FirstSeenMills"/> and <see cref="LastSeenMills"/> are computed from their
/// respective <see cref="DateTime"/> timestamp properties for v1/v3 API compatibility.
/// </para>
/// </remarks>
/// <seealso cref="DeviceCategory"/>
/// <seealso cref="PatientDevice"/>
/// <seealso cref="DeviceCatalog"/>
/// <seealso cref="DeviceCatalogEntry"/>
public class Device
{
    /// <summary>
    /// UUID v7 primary key
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Device category discriminator (e.g. <see cref="DeviceCategory.InsulinPump"/>,
    /// <see cref="DeviceCategory.CGM"/>, <see cref="DeviceCategory.Uploader"/>).
    /// </summary>
    public DeviceCategory Category { get; set; }

    /// <summary>
    /// Device type/model name (e.g. "Omnipod DASH", "Medtronic 780G")
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Device serial number
    /// </summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>
    /// When this device was first seen as UTC DateTime
    /// </summary>
    public DateTime FirstSeenTimestamp { get; set; }

    /// <summary>
    /// When this device was last seen as UTC DateTime
    /// </summary>
    public DateTime LastSeenTimestamp { get; set; }

    /// <summary>
    /// When this device was first seen in Unix milliseconds, computed from <see cref="FirstSeenTimestamp"/>.
    /// </summary>
    public long FirstSeenMills => new DateTimeOffset(FirstSeenTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// When this device was last seen in Unix milliseconds, computed from <see cref="LastSeenTimestamp"/>.
    /// </summary>
    public long LastSeenMills => new DateTimeOffset(LastSeenTimestamp, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// Catch-all for fields not mapped to dedicated columns
    /// </summary>
    public Dictionary<string, object?>? AdditionalProperties { get; set; }
}
