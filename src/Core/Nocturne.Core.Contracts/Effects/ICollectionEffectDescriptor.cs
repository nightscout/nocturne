namespace Nocturne.Core.Contracts.Effects;

/// <summary>
/// Describes the write side effects for a specific Nightscout collection.
/// Registered once at startup; resolved by collection name at runtime.
/// </summary>
public interface ICollectionEffectDescriptor
{
    /// <summary>
    /// The Nightscout collection name this descriptor applies to (e.g., "entries", "treatments").
    /// </summary>
    string CollectionName { get; }

    /// <summary>
    /// Get exact cache keys that should be removed after a write to this collection.
    /// </summary>
    /// <param name="tenantCacheId">Tenant-scoped cache ID prefix.</param>
    IReadOnlyList<string> GetCacheKeysToRemove(string tenantCacheId);

    /// <summary>
    /// Get cache key patterns (wildcards) that should be cleared after a write to this collection.
    /// </summary>
    /// <param name="tenantCacheId">Tenant-scoped cache ID prefix.</param>
    /// <returns>List of wildcard patterns to clear.</returns>
    IReadOnlyList<string> GetCachePatternsToClear(string tenantCacheId);

    /// <summary>
    /// Whether writes to this collection should trigger V4 decomposition
    /// (splitting legacy records into granular V4 tables).
    /// </summary>
    bool DecomposeToV4 { get; }

    /// <summary>
    /// Whether a create operation should additionally broadcast a data-update event
    /// via SignalR (used for glucose entries to trigger real-time dashboard updates).
    /// </summary>
    bool BroadcastDataUpdateOnCreate { get; }
}
