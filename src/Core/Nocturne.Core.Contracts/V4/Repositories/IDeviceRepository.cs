using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="Device"/> records that represent physical hardware devices
/// (pumps, CGMs, meters) associated with a patient.
/// </summary>
/// <remarks>
/// Devices are identified by their category, manufacturer type string, and serial number.
/// The <see cref="FindByCategoryTypeAndSerialAsync"/> method is used during data ingestion
/// to look up or upsert a device record before creating event and snapshot records linked to it.
/// </remarks>
/// <seealso cref="Device"/>
/// <seealso cref="DeviceCategory"/>
/// <seealso cref="IPatientDeviceRepository"/>
public interface IDeviceRepository
{
    /// <summary>Returns a <see cref="Device"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Look up a <see cref="Device"/> by its category, type string, and serial number.
    /// </summary>
    /// <remarks>
    /// Used during connector ingestion to find the existing device record before attaching new data,
    /// avoiding duplicate device rows for the same physical hardware.
    /// </remarks>
    /// <param name="category">Device category (e.g., Pump, CGM, Meter).</param>
    /// <param name="type">Manufacturer-defined type string (e.g., "t:slim X2").</param>
    /// <param name="serial">Device serial number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching <see cref="Device"/>, or <c>null</c> if not found.</returns>
    Task<Device?> FindByCategoryTypeAndSerialAsync(DeviceCategory category, string type, string serial, CancellationToken ct = default);

    /// <summary>Persist a new <see cref="Device"/> record and return the saved entity.</summary>
    /// <param name="model">Device record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Device> CreateAsync(Device model, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>Replace an existing <see cref="Device"/> identified by <paramref name="id"/>.</summary>
    /// <param name="id">UUID v7 identifier of the record to update.</param>
    /// <param name="model">Updated device data.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Device> UpdateAsync(Guid id, Device model, WriteOrigin origin, CancellationToken ct = default);
}
