using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="DeviceStatusExtras"/> records containing uncaptured devicestatus sub-objects.
/// </summary>
/// <remarks>
/// Alpha-phase diagnostic data. Records are created during devicestatus decomposition
/// and can be queried or deleted by their correlation ID.
/// </remarks>
public interface IDeviceStatusExtrasRepository
{
    /// <summary>Persist a new <see cref="DeviceStatusExtras"/> and return the saved entity.</summary>
    /// <param name="model">Record to create.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DeviceStatusExtras> CreateAsync(DeviceStatusExtras model, CancellationToken ct = default);

    /// <summary>Retrieve <see cref="DeviceStatusExtras"/> records matching any of the given correlation IDs.</summary>
    /// <param name="correlationIds">Correlation IDs to match.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IEnumerable<DeviceStatusExtras>> GetByCorrelationIdsAsync(IEnumerable<Guid> correlationIds, CancellationToken ct = default);

    /// <summary>Delete the <see cref="DeviceStatusExtras"/> record(s) with the given correlation ID.</summary>
    /// <param name="correlationId">Correlation ID to match.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByCorrelationIdAsync(Guid correlationId, CancellationToken ct = default);
}
