using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Effects;

/// <summary>
/// Coordinates the write side effects that run after a collection mutation:
/// <see cref="ITenantCache"/> invalidation and <see cref="ISignalRBroadcastService"/> real-time broadcasting.
/// </summary>
/// <remarks>
/// A broadcast does not throw (see <see cref="ISignalRBroadcastService"/>), so the write is never rolled back.
/// </remarks>
/// <seealso cref="IWriteSideEffects"/>
/// <seealso cref="ISignalRBroadcastService"/>
public class WriteSideEffectsService : IWriteSideEffects
{
    private readonly ITenantCache _cache;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly ILogger<WriteSideEffectsService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="WriteSideEffectsService"/>.
    /// </summary>
    /// <param name="cache">The tenant cache whose key families a write clears.</param>
    /// <param name="broadcast">The SignalR broadcast service for real-time storage event delivery.</param>
    /// <param name="logger">The logger instance.</param>
    public WriteSideEffectsService(
        ITenantCache cache,
        ISignalRBroadcastService broadcast,
        ILogger<WriteSideEffectsService> logger
    )
    {
        _cache = cache;
        _broadcast = broadcast;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sequence: cache invalidation, then per-record <c>create</c> SignalR broadcast, then optional
    /// <c>dataUpdate</c> broadcast if <see cref="WriteEffectOptions.BroadcastDataUpdate"/> is set.
    /// </remarks>
    public async Task OnCreatedAsync<T>(
        string collectionName,
        IReadOnlyList<T> records,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class
    {
        options ??= new WriteEffectOptions();

        await InvalidateCacheAsync(options, cancellationToken);

        foreach (var record in records)
        {
            await _broadcast.BroadcastStorageCreateAsync(
                collectionName,
                new StorageDocumentEvent(collectionName, record)
            );
        }

        if (options.BroadcastDataUpdate)
        {
            await _broadcast.BroadcastDataUpdateAsync(records.ToArray());
        }
    }

    /// <inheritdoc />
    public async Task OnUpdatedAsync<T>(
        string collectionName,
        T record,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class
    {
        options ??= new WriteEffectOptions();

        await InvalidateCacheAsync(options, cancellationToken);

        await _broadcast.BroadcastStorageUpdateAsync(
            collectionName,
            new StorageDocumentEvent(collectionName, record)
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// If <paramref name="deletedRecord"/> is <see langword="null"/>, the broadcast is skipped
    /// (cache is still invalidated). This is intentional for bulk deletes where the individual
    /// record is not fetched.
    /// </remarks>
    public async Task OnDeletedAsync<T>(
        string collectionName,
        T? deletedRecord,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class
    {
        options ??= new WriteEffectOptions();

        await InvalidateCacheAsync(options, cancellationToken);

        if (deletedRecord is null)
            return;

        await _broadcast.BroadcastStorageDeleteAsync(
            collectionName,
            new StorageDeleteEvent(
                collectionName,
                (deletedRecord as IProcessableDocument)?.Id,
                deletedRecord
            )
        );
    }

    /// <inheritdoc />
    public async Task OnBulkDeletedAsync(
        string collectionName,
        long deletedCount,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        if (deletedCount <= 0)
            return;

        options ??= new WriteEffectOptions();

        await InvalidateCacheAsync(options, cancellationToken);

        await _broadcast.BroadcastStorageDeleteAsync(
            collectionName,
            new StorageBulkDeleteEvent(collectionName, deletedCount)
        );
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exceptions are caught and logged as warnings, because a cache failure must not abort the write path.
    /// </remarks>
    public async Task InvalidateCacheAsync(WriteEffectOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var family in options.CacheFamiliesToClear)
            {
                await _cache.RemoveFamilyAsync(family, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation failed");
        }
    }
}
