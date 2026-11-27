using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// <see cref="IDataEventSink{T}"/> implementation for <see cref="Entry"/> that translates
/// write lifecycle events (created, updated, deleted, bulk-deleted) into the legacy <c>entries</c>
/// storage broadcasts of <see cref="ISignalRBroadcastService"/>.
/// </summary>
/// <remarks>
/// The only cache this clears is eHbA1c: the years a written reading reaches (<see cref="OnWrittenAsync"/>),
/// or all of them after a bulk delete. The entry reads are cleared by
/// <see cref="Nocturne.Core.Contracts.V4.ILegacyReadCacheInvalidation"/>.
/// <c>dataUpdate</c> broadcasts are suppressed on single-record updates to avoid duplicate client refreshes.
/// A broadcast does not throw (<see cref="ISignalRBroadcastService"/>), and a failed cache clear is logged,
/// so nothing here fails the write that has already committed.
/// </remarks>
/// <seealso cref="IDataEventSink{T}"/>
/// <seealso cref="EntryService"/>
internal class SignalREntryEventSink : IDataEventSink<Entry>
{
    private readonly ITenantCache _cache;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ILogger<SignalREntryEventSink> _logger;
    private const string CollectionName = "entries";

    /// <summary>
    /// Initializes a new instance of <see cref="SignalREntryEventSink"/>.
    /// </summary>
    /// <param name="cache">The tenant cache whose eHbA1c families a write clears.</param>
    /// <param name="broadcast">Sends the storage events.</param>
    /// <param name="logger">The logger instance.</param>
    public SignalREntryEventSink(
        ITenantCache cache,
        ISignalRBroadcastService broadcast,
        ILogger<SignalREntryEventSink> logger)
    {
        _cache = cache;
        _broadcast = broadcast;
        _logger = logger;
    }

    public async Task OnCreatedAsync(IReadOnlyList<Entry> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
            await _broadcast.BroadcastStorageCreateAsync(CollectionName, new StorageDocumentEvent(CollectionName, entry));

        await _broadcast.BroadcastDataUpdateAsync(entries.ToArray());
    }

    /// <summary>
    /// Clears the eHbA1c years the written readings feed, for every write origin: a completed year's
    /// timeline is cached for weeks, and a backfill or import is the write most likely to land in one.
    /// </summary>
    public async Task OnWrittenAsync(IReadOnlyList<Entry> entries, CancellationToken ct = default)
    {
        var readings = entries
            .Where(e => e.Type is "sgv" or "mbg")
            .Select(e => e.Date)
            .OfType<DateTime>()
            .ToList();
        if (readings.Count == 0)
            return;

        await ClearAsync(DataOverviewService.EHbA1cCacheFamilies(readings), ct);
    }

    public async Task OnUpdatedAsync(Entry entry, CancellationToken ct = default)
    {
        await _broadcast.BroadcastStorageUpdateAsync(CollectionName, new StorageDocumentEvent(CollectionName, entry));
    }

    /// <remarks>
    /// Without the deleted entry nothing is sent: a bulk path that did not read the entry it removed
    /// has no identifier to give.
    /// </remarks>
    public async Task OnDeletedAsync(Entry? entry, CancellationToken ct = default)
    {
        if (entry is null)
            return;

        await _broadcast.BroadcastStorageDeleteAsync(
            CollectionName, new StorageDeleteEvent(CollectionName, (entry as IProcessableDocument)?.Id, entry));
    }

    public async Task OnBulkDeletedAsync(long deletedCount, CancellationToken ct = default)
    {
        if (deletedCount <= 0)
            return;

        await ClearAsync([TenantCacheFamily.EHbA1c], ct);
        await _broadcast.BroadcastStorageDeleteAsync(CollectionName, new StorageBulkDeleteEvent(CollectionName, deletedCount));
    }

    /// <remarks>A cache failure must not fail a write that has already committed, so it is logged.</remarks>
    private async Task ClearAsync(IEnumerable<string> families, CancellationToken ct)
    {
        try
        {
            foreach (var family in families)
                await _cache.RemoveFamilyAsync(family, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear the eHbA1c cache after an entry write");
        }
    }
}
