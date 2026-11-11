using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.DeviceStatuses;

/// <summary>
/// <see cref="IDeviceStatusService"/> over <see cref="IDeviceStatusStore"/> for reads and
/// <see cref="IDeviceStatusDecomposer"/> for writes.
/// </summary>
internal sealed class DeviceStatusService(
    IDeviceStatusStore store,
    IDeviceStatusDecomposer decomposer,
    IWriteSideEffects sideEffects,
    IDataEventSink<DeviceStatus> events,
    ILogger<DeviceStatusService> logger) : IDeviceStatusService
{
    private const string CollectionName = "devicestatus";

    private const int BulkDeleteLimit = 10_000;

    public Task<IReadOnlyList<DeviceStatus>> GetDeviceStatusesAsync(
        FindQuery find, int count, int skip, CancellationToken cancellationToken,
        bool descending = true, ReadSortField sort = ReadSortField.Time) =>
        store.GetMatchingAsync(find, count, skip, cancellationToken, descending, sort);

    public Task<long> CountDeviceStatusesAsync(FindQuery find, CancellationToken cancellationToken) =>
        store.CountMatchingAsync(find, cancellationToken);

    public Task<DeviceStatus?> GetDeviceStatusByIdAsync(string id, CancellationToken cancellationToken) =>
        store.GetByIdAsync(id, cancellationToken);

    public Task<IEnumerable<DeviceStatus>> GetDeviceStatusesModifiedSinceAsync(
        long lastModified, int limit, CancellationToken cancellationToken) =>
        store.GetModifiedSinceAsync(lastModified, limit, cancellationToken);

    public bool HasLegacyKeyedSnapshot(DeviceStatus status) => decomposer.HasLegacyKeyedSnapshot(status);

    /// <remarks>
    /// The announcement runs whatever ends the batch, and not on the request's token: a status that was
    /// written stays written when the request is cancelled or the batch fails part-way, and a client that
    /// missed its broadcast, or an upstream that missed its write-back, would never learn of it.
    /// </remarks>
    public async Task<BulkWrite<DeviceStatus>> CreateDeviceStatusesAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source = null, WriteOrigin origin = WriteOrigin.Live,
        CancellationToken cancellationToken = default)
    {
        var announce = origin == WriteOrigin.Live;
        var result = new DeviceStatusDecomposition();
        IReadOnlyList<DeviceStatus> served = [];
        try
        {
            await decomposer.DecomposeBatchAsync(statuses, source, origin, cancellationToken, result);
        }
        finally
        {
            if (announce)
                served = await AnnounceCreatedAsync(statuses, result.Wrote, source);
        }

        return new BulkWrite<DeviceStatus>(announce ? served : statuses, result.SkippedDeleted);
    }

    public async Task<DecompositionResult> ImportDeviceStatusesAsync(
        IReadOnlyList<DeviceStatus> statuses, string? source, CancellationToken cancellationToken = default) =>
        await decomposer.DecomposeBatchAsync(statuses, source, WriteOrigin.Backfill, cancellationToken);

    /// <remarks>
    /// The id may be only the wire form of the stored one, and the stored snapshots are found by the
    /// stored id.
    /// </remarks>
    public async Task<DeviceStatus?> ReplaceDeviceStatusAsync(
        string id, DeviceStatus status, CancellationToken cancellationToken = default)
    {
        if (await store.GetByIdAsync(id, cancellationToken) is not { Id: { } storedId }
            || await decomposer.ReplaceAsync(storedId, status, WriteOrigin.Live, cancellationToken) is null)
            return null;

        var updated = await store.GetByIdAsync(status.Id!, cancellationToken) ?? status;

        await sideEffects.OnUpdatedAsync(CollectionName, updated, cancellationToken: cancellationToken);
        await events.OnUpdatedAsync(updated, cancellationToken);

        return updated;
    }

    /// <remarks>
    /// The id may be only the wire form of the stored one; the status read under it is also what the
    /// delete announces.
    /// </remarks>
    public async Task<bool> DeleteDeviceStatusAsync(string id, CancellationToken cancellationToken = default)
    {
        if (await store.GetByIdAsync(id, cancellationToken) is not { Id: { } storedId } deleted
            || await decomposer.DeleteStoredAsync(storedId, WriteOrigin.Live, cancellationToken) == 0)
            return false;

        await sideEffects.OnDeletedAsync(CollectionName, deleted, cancellationToken: cancellationToken);
        await events.OnDeletedAsync(deleted, cancellationToken);

        return true;
    }

    public async Task<long> DeleteDeviceStatusesAsync(FindQuery find, CancellationToken cancellationToken = default)
    {
        var matching = await store.GetMatchingAsync(find, BulkDeleteLimit, skip: 0, cancellationToken);
        if (matching.Count == BulkDeleteLimit)
            logger.LogWarning(
                "Bulk delete matched the maximum of {Limit} records — results may be truncated", BulkDeleteLimit);

        long deletedCount = 0;
        foreach (var status in matching)
        {
            if (!string.IsNullOrEmpty(status.Id)
                && await decomposer.DeleteStoredAsync(status.Id, WriteOrigin.Live, cancellationToken) > 0)
                deletedCount++;
        }

        await sideEffects.OnBulkDeletedAsync(CollectionName, deletedCount, cancellationToken: cancellationToken);
        await events.OnBulkDeletedAsync(deletedCount, cancellationToken);

        return deletedCount;
    }

    /// <summary>
    /// Reads each status back as reads serve it, and announces those that wrote a record, which before
    /// the batch resolved its groups is none. A read carries no data source, so each is stamped with
    /// <paramref name="source"/> for the write-back to skip on.
    /// </summary>
    /// <returns>Every status, as read back or else as given.</returns>
    private async Task<IReadOnlyList<DeviceStatus>> AnnounceCreatedAsync(
        IReadOnlyList<DeviceStatus> statuses, IReadOnlyList<bool> wrote, string? source)
    {
        var served = new List<DeviceStatus>(statuses.Count);
        var announced = new List<DeviceStatus>(statuses.Count);
        for (var i = 0; i < statuses.Count; i++)
        {
            var read = await ReadBackAsync(statuses[i]) ?? statuses[i];
            read.DataSource = source;
            served.Add(read);
            if (wrote.Count == statuses.Count && wrote[i])
                announced.Add(read);
        }

        if (announced.Count > 0)
        {
            await sideEffects.OnCreatedAsync(CollectionName, announced, cancellationToken: CancellationToken.None);
            await events.OnCreatedAsync(announced, CancellationToken.None);
        }

        return served;
    }

    /// <returns>
    /// The status as stored, or <see langword="null"/> when it has no id or could not be read back; the
    /// caller then serves it as given.
    /// </returns>
    private async Task<DeviceStatus?> ReadBackAsync(DeviceStatus status)
    {
        if (string.IsNullOrEmpty(status.Id))
            return null;

        try
        {
            return await store.GetByIdAsync(status.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Created device status {Id} could not be read back", status.Id);
            return null;
        }
    }
}
