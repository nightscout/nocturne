using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="Device"/> records that represent physical hardware devices
/// (pumps, CGMs, meters) associated with a patient.
/// </summary>
/// <remarks>
/// Devices are identified by their category, manufacturer type string, and serial number.
/// The <see cref="FindByIdentitiesAsync"/> method is used during data ingestion
/// to look up the device records before creating event and snapshot records linked to it.
/// </remarks>
/// <seealso cref="Device"/>
/// <seealso cref="DeviceCategory"/>
/// <seealso cref="IPatientDeviceRepository"/>
public interface IDeviceRepository
{
    /// <summary>Returns a <see cref="Device"/> by its UUID v7, or <c>null</c> if not found.</summary>
    /// <param name="id">UUID v7 record identifier.</param>
    Task<Device?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The stored <see cref="Device"/> of each of <paramref name="identities"/> that has one, in one query.
    /// </summary>
    Task<IReadOnlyList<Device>> FindByIdentitiesAsync(
        IReadOnlyCollection<(DeviceCategory Category, string Type, string Serial)> identities,
        CancellationToken ct = default);

    /// <summary>Persist a new <see cref="Device"/> record and return the saved entity.</summary>
    Task<Device> CreateAsync(Device model, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Widen the stored seen window of the <see cref="Device"/> identified by <paramref name="id"/> to
    /// include <paramref name="timestamp"/>: last seen only ever moves later and first seen only ever earlier.
    /// </summary>
    /// <remarks>
    /// The comparison runs inside the UPDATE against the stored row rather than against a copy the
    /// caller read, so a scope holding a stale device cannot move either bound back over a concurrent
    /// writer's.
    /// </remarks>
    /// <param name="id">UUID v7 identifier of the device.</param>
    /// <param name="timestamp">UTC instant the device was seen at.</param>
    Task WidenSeenWindowAsync(Guid id, DateTime timestamp, WriteOrigin origin, CancellationToken ct = default);
}
