using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="SensorStateReport"/> records, the sensor states device statuses
/// reported. A status's row is keyed by its correlation id, as its <see cref="DeviceStatusExtras"/>
/// row is: a create whose correlation id a stored row holds, live or deleted by the user, is skipped.
/// </summary>
public interface ISensorStateReportRepository : IBulkCreateRepository<SensorStateReport>
{
    /// <summary>
    /// The live reports of <paramref name="device"/> under <paramref name="dataSource"/> stored by
    /// <paramref name="asOf"/>, whose timestamp and arrival are both at or after <paramref name="since"/>
    /// and whose timestamp is at or before <paramref name="latestTimestamp"/>.
    /// </summary>
    Task<IReadOnlyList<SensorStateReport>> GetReportedAsync(
        string device, string dataSource, DateTime since, DateTime asOf, DateTime latestTimestamp,
        CancellationToken ct = default);

    /// <summary>Delete the report of the status with the given correlation id.</summary>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByCorrelationIdAsync(Guid correlationId, WriteOrigin origin, CancellationToken ct = default);
}
