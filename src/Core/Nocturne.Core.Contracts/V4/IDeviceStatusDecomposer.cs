using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Decomposes a legacy DeviceStatus record into typed v4 snapshot tables.
/// </summary>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="IEntryDecomposer"/>
public interface IDeviceStatusDecomposer
{
    /// <summary>
    /// Extracts APS, pump, and uploader snapshots from a DeviceStatus record
    /// and persists them to v4 tables. Idempotent via LegacyId matching.
    /// </summary>
    /// <param name="deviceStatus">The legacy DeviceStatus record to decompose.</param>
    /// <param name="source">Connector data source to stamp on the decomposed snapshots, or
    /// <c>null</c> for direct v1/v3 uploads with no connector origin.</param>
    /// <param name="origin">Write classification used by the repository chokepoint to decide whether
    /// to broadcast (live) or stay silent (backfill).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DecompositionResult> DecomposeAsync(DeviceStatus deviceStatus, string? source, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Decomposes a batch of DeviceStatus records into typed v4 snapshot tables using bulk-insert
    /// operations to eliminate N+1 DB round-trips. Pump suspension state spans are processed as a
    /// post-insert sequential pass since transition detection depends on prior committed snapshots.
    /// </summary>
    /// <param name="statuses">DeviceStatus records to decompose.</param>
    /// <param name="source">Connector data source to stamp on the decomposed snapshots, or
    /// <c>null</c> for direct v1/v3 uploads with no connector origin.</param>
    /// <param name="origin">Write classification used by the repository chokepoint to decide whether
    /// to broadcast (live) or stay silent (backfill).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DecompositionResult> DecomposeBatchAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Deletes all v4 snapshot records that were decomposed from a legacy DeviceStatus with the given ID.
    /// </summary>
    /// <param name="legacyId">The legacy DeviceStatus ID</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Total number of v4 records deleted across all snapshot tables</returns>
    Task<int> DeleteByLegacyIdAsync(string legacyId, WriteOrigin origin, CancellationToken ct = default);
}
