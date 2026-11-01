using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>
/// Decomposes a legacy DeviceStatus record into typed v4 snapshot tables.
/// </summary>
internal interface IDeviceStatusDecomposer
{
    /// <summary>
    /// Replaces the stored device status <paramref name="storedId"/> resolves to with
    /// <paramref name="deviceStatus"/>, as a legacy v3 PUT replaces the whole document: each section
    /// it carries rewrites the stored snapshot in place, and each section it omits is removed.
    /// </summary>
    /// <param name="storedId">The id the stored status projects under: its legacy id, or its anchoring
    /// snapshot's uuid when it has none.</param>
    /// <param name="deviceStatus">The replacement document. Its id is set to the legacy id the group is
    /// written under.</param>
    /// <param name="origin">Write classification, as for <see cref="DecomposeBatchAsync"/>.</param>
    /// <returns>The decomposition, or <see langword="null"/> when nothing is stored under
    /// <paramref name="storedId"/>.</returns>
    Task<DecompositionResult?> ReplaceAsync(
        string storedId, DeviceStatus deviceStatus, WriteOrigin origin, CancellationToken ct = default);

    /// <summary>
    /// Whether decomposing <paramref name="deviceStatus"/> stores an APS, pump or uploader snapshot,
    /// the rows that hold a device status under its legacy id. A status carrying none of them leaves
    /// nothing a later lookup by id can find.
    /// </summary>
    bool HasLegacyKeyedSnapshot(DeviceStatus deviceStatus);

    /// <summary>
    /// Extracts APS, pump and uploader snapshots, extras and override and pump-mode spans from the
    /// statuses and persists them to v4 tables, idempotently via legacy-id matching, in a fixed number
    /// of queries whatever the batch's size. A single status is a batch of one.
    /// </summary>
    /// <param name="source">Connector data source to stamp on the decomposed snapshots, or
    /// <c>null</c> for direct v1/v3 uploads with no connector origin.</param>
    /// <param name="origin">Write classification used by the repository chokepoint to decide whether
    /// to broadcast (live) or stay silent (backfill).</param>
    /// <param name="into">The decomposition to fill as each write commits, for a caller that must know
    /// what committed when the batch throws part-way.</param>
    Task<DeviceStatusDecomposition> DecomposeBatchAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source, WriteOrigin origin, CancellationToken ct = default,
        DeviceStatusDecomposition? into = null);

    /// <summary>
    /// Deletes every v4 record of the stored device status <paramref name="storedId"/> resolves to:
    /// its snapshots, extras and the override stored under its key.
    /// </summary>
    /// <param name="storedId">The id the stored status projects under: its legacy id, or its anchoring
    /// snapshot's uuid when it has none.</param>
    /// <param name="origin">Write classification, as for <see cref="DecomposeBatchAsync"/>.</param>
    /// <returns>Total number of v4 records deleted; 0 when nothing is stored under the id.</returns>
    Task<int> DeleteStoredAsync(string storedId, WriteOrigin origin, CancellationToken ct = default);
}
