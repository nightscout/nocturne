namespace Nocturne.Core.Contracts.Effects;

/// <summary>
/// Orchestrates post-write side effects for data services: cache invalidation,
/// SignalR broadcasting, and V4 decomposition. All effects are fire-and-forget —
/// errors are caught and logged, never propagated to callers.
/// </summary>
public interface IWriteSideEffects
{
    /// <summary>
    /// Run all post-create side effects: invalidate cache, broadcast storage-create,
    /// optionally broadcast data-update, and decompose into V4 tables.
    /// </summary>
    /// <typeparam name="T">The domain record type that was created.</typeparam>
    /// <param name="collectionName">Nightscout collection name (e.g., "entries", "treatments").</param>
    /// <param name="records">The newly created records.</param>
    /// <param name="options">Optional effect configuration; defaults to no extra effects.</param>
    Task OnCreatedAsync<T>(
        string collectionName,
        IReadOnlyList<T> records,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Run all post-update side effects: invalidate cache, broadcast storage-update,
    /// and re-decompose into V4 tables.
    /// </summary>
    /// <typeparam name="T">The domain record type that was updated.</typeparam>
    /// <param name="collectionName">Nightscout collection name.</param>
    /// <param name="options">Optional effect configuration.</param>
    Task OnUpdatedAsync<T>(
        string collectionName,
        T record,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Run pre-delete cleanup: remove V4 records by legacy ID before the DB delete.
    /// Call this BEFORE the repository delete.
    /// </summary>
    /// <typeparam name="T">The domain record type about to be deleted.</typeparam>
    /// <param name="legacyId">The legacy record identifier used to locate V4 records for cleanup.</param>
    /// <param name="options">Optional effect configuration.</param>
    Task BeforeDeleteAsync<T>(
        string legacyId,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Run all post-delete side effects: invalidate cache and broadcast storage-delete.
    /// Call this AFTER the repository delete succeeds.
    /// </summary>
    /// <typeparam name="T">The domain record type that was deleted.</typeparam>
    /// <param name="collectionName">Nightscout collection name.</param>
    /// <param name="deletedRecord">The deleted record, or <c>null</c> if it was not available.</param>
    /// <param name="options">Optional effect configuration.</param>
    Task OnDeletedAsync<T>(
        string collectionName,
        T? deletedRecord,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Run post-bulk-delete side effects: invalidate cache and broadcast aggregate delete count.
    /// </summary>
    /// <param name="collectionName">Nightscout collection name.</param>
    /// <param name="options">Optional effect configuration.</param>
    Task OnBulkDeletedAsync(
        string collectionName,
        long deletedCount,
        WriteEffectOptions? options = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Immutable configuration for which side effects to run and how.
/// Construct as static readonly per service — these never change at runtime.
/// </summary>
public sealed record WriteEffectOptions
{
    /// <summary>Tenant cache key families to clear, in every partition, after a write.</summary>
    public IReadOnlyList<string> CacheFamiliesToClear { get; init; } = [];

    /// <summary>Whether to run IDecompositionPipeline after create/update.</summary>
    public bool DecomposeToV4 { get; init; }

    /// <summary>Whether to also call BroadcastDataUpdateAsync after create (Entry glucose only).</summary>
    public bool BroadcastDataUpdate { get; init; }
}
