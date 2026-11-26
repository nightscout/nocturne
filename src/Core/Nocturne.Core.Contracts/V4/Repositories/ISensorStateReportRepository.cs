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
    /// The newest live report of <paramref name="device"/> under <paramref name="dataSource"/> stored by
    /// <paramref name="asOf"/>, whose timestamp and arrival are both at or after <paramref name="since"/>
    /// and whose timestamp is at or before <paramref name="latestTimestamp"/>, with its arrival; null
    /// when there is none.
    /// </summary>
    /// <remarks>
    /// Newest is by arrival (<see cref="ArrivedSensorStateReport.ArrivedAt"/>), so a report dated ahead
    /// does not outrank a newer one. Of reports that arrived at the same instant, a state that is not ok
    /// wins, then no state, then ok, then the lower id, so the pick never depends on row order. Only a
    /// state that is not ok holds a glucose rule, so a report that flagged the sensor is not outvoted by
    /// one that arrived with it; no state ranks above ok since it claims nothing about the sensor.
    /// </remarks>
    Task<ArrivedSensorStateReport?> GetNewestAsync(
        string device, string dataSource, DateTime since, DateTime asOf, DateTime latestTimestamp,
        CancellationToken ct = default);

    /// <summary>Delete the report of the status with the given correlation id.</summary>
    /// <returns>Number of records deleted.</returns>
    Task<int> DeleteByCorrelationIdAsync(Guid correlationId, WriteOrigin origin, CancellationToken ct = default);
}
