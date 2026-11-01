using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Models;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Entries;

/// <summary>
/// <see cref="IDataEventSink{T}"/> implementation for <see cref="Entry"/> that translates
/// write lifecycle events (created, updated, deleted, bulk-deleted) into
/// <see cref="ISignalRBroadcastService"/> broadcasts via <see cref="IWriteSideEffects"/>.
/// </summary>
/// <remarks>
/// The only cache this clears is eHbA1c: the years a written reading reaches (<see cref="OnWrittenAsync"/>),
/// or all of them after a bulk delete. The entry reads are cleared by
/// <see cref="Nocturne.Core.Contracts.V4.ILegacyReadCacheInvalidation"/>.
/// <c>dataUpdate</c> broadcasts are suppressed on single-record updates to avoid duplicate client refreshes.
/// </remarks>
/// <seealso cref="IDataEventSink{T}"/>
/// <seealso cref="IWriteSideEffects"/>
/// <seealso cref="EntryService"/>
internal class SignalREntryEventSink : IDataEventSink<Entry>
{
    private readonly IWriteSideEffects _sideEffects;
    private readonly ILogger<SignalREntryEventSink> _logger;
    private const string CollectionName = "entries";

    /// <summary>
    /// Initializes a new instance of <see cref="SignalREntryEventSink"/>.
    /// </summary>
    /// <param name="sideEffects">Orchestrates cache invalidation and broadcasting.</param>
    /// <param name="logger">The logger instance.</param>
    public SignalREntryEventSink(
        IWriteSideEffects sideEffects,
        ILogger<SignalREntryEventSink> logger)
    {
        _sideEffects = sideEffects;
        _logger = logger;
    }

    private WriteEffectOptions BuildWriteOptions() => new()
    {
        BroadcastDataUpdate = true,
    };

    private WriteEffectOptions BuildWriteOptionsNoBroadcastDataUpdate() => new()
    {
        BroadcastDataUpdate = false,
    };

    private WriteEffectOptions BuildCacheOnlyOptions() => new()
    {
        CacheFamiliesToClear = [TenantCacheFamily.EHbA1c],
    };

    public async Task OnCreatedAsync(IReadOnlyList<Entry> entries, CancellationToken ct = default)
    {
        try
        {
            await _sideEffects.OnCreatedAsync(CollectionName, entries, BuildWriteOptions(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process create side effects for {Count} entries", entries.Count);
        }
    }

    /// <summary>
    /// Clears the eHbA1c years the written readings feed, for every write origin: a completed year's
    /// timeline is cached for weeks, and a backfill or import is the write most likely to land in one.
    /// </summary>
    public async Task OnWrittenAsync(IReadOnlyList<Entry> entries, CancellationToken ct = default)
    {
        try
        {
            var readings = entries
                .Where(e => e.Type is "sgv" or "mbg")
                .Select(e => e.Date)
                .OfType<DateTime>()
                .ToList();
            if (readings.Count == 0)
                return;

            await _sideEffects.InvalidateCacheAsync(
                new WriteEffectOptions { CacheFamiliesToClear = DataOverviewService.EHbA1cCacheFamilies(readings) },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear eHbA1c cache for {Count} written entries", entries.Count);
        }
    }

    public async Task OnUpdatedAsync(Entry entry, CancellationToken ct = default)
    {
        try
        {
            await _sideEffects.OnUpdatedAsync(CollectionName, entry, BuildWriteOptionsNoBroadcastDataUpdate(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process update side effects for entry {Id}", entry.Id);
        }
    }

    public async Task OnDeletedAsync(Entry? entry, CancellationToken ct = default)
    {
        try
        {
            await _sideEffects.OnDeletedAsync(CollectionName, entry, BuildWriteOptions(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process delete side effects for entry {Id}", entry?.Id);
        }
    }

    public async Task OnBulkDeletedAsync(long deletedCount, CancellationToken ct = default)
    {
        try
        {
            await _sideEffects.OnBulkDeletedAsync(CollectionName, deletedCount, BuildCacheOnlyOptions(), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process bulk delete side effects for {Count} entries", deletedCount);
        }
    }
}
