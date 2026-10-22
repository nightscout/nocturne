using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="DeviceStatusExtras"/> records containing uncaptured devicestatus sub-objects.
/// </summary>
/// <remarks>
/// Alpha-phase diagnostic data. Records are created during devicestatus decomposition
/// and can be queried or deleted by their correlation ID.
/// </remarks>
public interface IDeviceStatusExtrasRepository : IBulkCreateRepository<DeviceStatusExtras>
{
    /// <summary>Persist a new <see cref="DeviceStatusExtras"/> and return the saved entity.</summary>
    Task<DeviceStatusExtras> CreateAsync(DeviceStatusExtras model, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>The <see cref="DeviceStatusExtras"/> with the given id, or <c>null</c>.</summary>
    Task<DeviceStatusExtras?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The first <see cref="DeviceStatusExtras"/> whose id falls within <c>[low, high]</c>, as
    /// <see cref="IV4Repository{T}.GetByGuidRangeAsync"/> resolves a 24-hex ObjectId.
    /// </summary>
    Task<DeviceStatusExtras?> GetByGuidRangeAsync(Guid low, Guid high, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="DeviceStatusExtras"/> records matching any of the given correlation IDs.</summary>
    /// <param name="correlationIds">Correlation IDs to match.</param>
    Task<IEnumerable<DeviceStatusExtras>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>Delete the <see cref="DeviceStatusExtras"/> record(s) with the given correlation ID.</summary>
    /// <param name="correlationId">Correlation ID to match.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);
}
