namespace Nocturne.Core.Models.V4;

/// <summary>
/// The outcome of restoring many soft-deleted records at once.
/// </summary>
/// <typeparam name="T">The restored record type.</typeparam>
public class BulkRestoreResult<T>
{
    public IReadOnlyList<T> Restored { get; init; } = [];

    /// <summary>
    /// Ids left deleted because a unique key the record would take, such as its legacy id or sync
    /// identifier, is already held: by a live record, or by another id restored in the same call,
    /// where the older deletion loses. An id listed only for the second cause restores on its own.
    /// </summary>
    public IReadOnlyList<Guid> Conflicts { get; init; } = [];

    public BulkRestoreResult<TOut> Map<TOut>(Func<T, TOut> map)
        => new() { Restored = Restored.Select(map).ToList(), Conflicts = Conflicts };
}
