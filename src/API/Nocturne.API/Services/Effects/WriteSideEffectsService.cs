using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using Nocturne.API.Services.Realtime;

namespace Nocturne.API.Services.Effects;

/// <summary>
/// Coordinates the three write side effects that must run after any collection mutation:
/// distributed cache invalidation, <see cref="ISignalRBroadcastService"/> real-time broadcasting,
/// and V4 decomposition via <see cref="IDecompositionPipeline"/>.
/// </summary>
/// <remarks>
/// Effect parameters are resolved from registered <see cref="ICollectionEffectDescriptor"/> instances
/// keyed by collection name (e.g. <c>"devicestatus"</c>, <c>"food"</c>, <c>"profile"</c>).
/// Callers may pass explicit <see cref="WriteEffectOptions"/> to override the descriptor defaults.
/// Broadcast failures are caught and logged without propagating — the write itself is never rolled back.
/// </remarks>
/// <seealso cref="IWriteSideEffects"/>
/// <seealso cref="ISignalRBroadcastService"/>
/// <seealso cref="IDecompositionPipeline"/>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class WriteSideEffectsService : IWriteSideEffects
{
    private readonly ICacheService _cache;
    private readonly ISignalRBroadcastService _broadcast;
    private readonly IDecompositionPipeline _pipeline;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IReadOnlyDictionary<string, ICollectionEffectDescriptor> _descriptors;
    private readonly ILogger<WriteSideEffectsService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="WriteSideEffectsService"/>.
    /// </summary>
    /// <param name="cache">The distributed cache for key/pattern-based invalidation.</param>
    /// <param name="broadcast">The SignalR broadcast service for real-time storage event delivery.</param>
    /// <param name="pipeline">The V4 decomposition pipeline for projecting legacy records into typed V4 tables.</param>
    /// <param name="tenantAccessor">Provides the current tenant context for scoping cache keys.</param>
    /// <param name="descriptors">Collection-level effect descriptors that define which cache keys to clear and whether to decompose.</param>
    /// <param name="logger">The logger instance.</param>
    public WriteSideEffectsService(
        ICacheService cache,
        ISignalRBroadcastService broadcast,
        IDecompositionPipeline pipeline,
        ITenantAccessor tenantAccessor,
        IEnumerable<ICollectionEffectDescriptor> descriptors,
        ILogger<WriteSideEffectsService> logger
    )
    {
        _cache = cache;
        _broadcast = broadcast;
        _pipeline = pipeline;
        _tenantAccessor = tenantAccessor;
        _descriptors = descriptors.ToDictionary(
            d => d.CollectionName,
            StringComparer.OrdinalIgnoreCase
        );
        _logger = logger;
    }

    /// <summary>
    /// Returns the explicit options if provided; otherwise builds <see cref="WriteEffectOptions"/>
    /// from the registered <see cref="ICollectionEffectDescriptor"/> for the given collection name.
    /// Falls back to an empty <see cref="WriteEffectOptions"/> if no descriptor is registered.
    /// </summary>
    /// <param name="collectionName">The collection name (e.g. <c>"devicestatus"</c>).</param>
    /// <param name="explicitOptions">Caller-supplied options that bypass the descriptor, or <see langword="null"/> to use the descriptor.</param>
    /// <returns>The resolved <see cref="WriteEffectOptions"/>.</returns>
    private WriteEffectOptions ResolveOptions(
        string collectionName,
        WriteEffectOptions? explicitOptions
    )
    {
        if (explicitOptions is not null)
            return explicitOptions;
        if (!_descriptors.TryGetValue(collectionName, out var descriptor))
            return new WriteEffectOptions();

        var tenantId = _tenantAccessor.Context?.TenantId.ToString() ?? "";
        return new WriteEffectOptions
        {
            CacheKeysToRemove = descriptor.GetCacheKeysToRemove(tenantId),
            CachePatternsToClear = descriptor.GetCachePatternsToClear(tenantId),
            DecomposeToV4 = descriptor.DecomposeToV4,
            BroadcastDataUpdate = descriptor.BroadcastDataUpdateOnCreate,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sequence: cache invalidation, then per-record <c>create</c> SignalR broadcast, then optional
    /// <c>dataUpdate</c> broadcast if <see cref="WriteEffectOptions.BroadcastDataUpdate"/> is set,
    /// then V4 decomposition if <see cref="WriteEffectOptions.DecomposeToV4"/> is set.
    /// Broadcast failures are swallowed and logged.
    /// </remarks>
    public async Task OnCreatedAsync<T>(
        string collectionName,
        IReadOnlyList<T> records,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class
    {
        options = ResolveOptions(collectionName, options);

        await InvalidateCacheAsync(options, cancellationToken);

        try
        {
            foreach (var record in records)
            {
                await _broadcast.BroadcastStorageCreateAsync(
                    collectionName,
                    new { colName = collectionName, doc = record }
                );
            }

            if (options.BroadcastDataUpdate)
            {
                await _broadcast.BroadcastDataUpdateAsync(records.ToArray());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Broadcast failed during create for {Collection}",
                collectionName
            );
        }

        if (options.DecomposeToV4)
        {
            await _pipeline.DecomposeAsync((IEnumerable<T>)records, WriteOrigin.Live, cancellationToken);
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
        options = ResolveOptions(collectionName, options);

        await InvalidateCacheAsync(options, cancellationToken);

        try
        {
            await _broadcast.BroadcastStorageUpdateAsync(
                collectionName,
                new { colName = collectionName, doc = record }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Broadcast failed during update for {Collection}",
                collectionName
            );
        }

        if (options.DecomposeToV4)
        {
            await _pipeline.DecomposeAsync(record, WriteOrigin.Live, cancellationToken);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only runs V4 decomposition cleanup when <see cref="WriteEffectOptions.DecomposeToV4"/> is set.
    /// Cache invalidation and broadcasts are deferred to <see cref="OnDeletedAsync{T}"/> after the delete completes.
    /// </remarks>
    public async Task BeforeDeleteAsync<T>(
        string legacyId,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class
    {
        options ??= new WriteEffectOptions();

        if (options.DecomposeToV4)
        {
            await _pipeline.DeleteByLegacyIdAsync<T>(legacyId, WriteOrigin.Live, cancellationToken);
        }
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
        options = ResolveOptions(collectionName, options);

        await InvalidateCacheAsync(options, cancellationToken);

        if (deletedRecord is null)
            return;

        try
        {
            await _broadcast.BroadcastStorageDeleteAsync(
                collectionName,
                new { colName = collectionName, doc = deletedRecord }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Broadcast failed during delete for {Collection}",
                collectionName
            );
        }
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

        options = ResolveOptions(collectionName, options);

        await InvalidateCacheAsync(options, cancellationToken);

        try
        {
            await _broadcast.BroadcastStorageDeleteAsync(
                collectionName,
                new { colName = collectionName, deletedCount }
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Broadcast failed during bulk delete for {Collection}",
                collectionName
            );
        }
    }

    /// <summary>
    /// Removes explicit cache keys and clears cache entries matching configured patterns.
    /// Exceptions are caught and logged as warnings — cache failures must not abort the write path.
    /// </summary>
    /// <param name="options">The effect options containing keys and patterns to invalidate.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task InvalidateCacheAsync(WriteEffectOptions options, CancellationToken ct)
    {
        try
        {
            foreach (var key in options.CacheKeysToRemove)
            {
                await _cache.RemoveAsync(key, ct);
            }

            foreach (var pattern in options.CachePatternsToClear)
            {
                await _cache.RemoveByPatternAsync(pattern, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation failed");
        }
    }
}
