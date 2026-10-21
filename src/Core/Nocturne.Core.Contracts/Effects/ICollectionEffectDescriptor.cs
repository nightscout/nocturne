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
