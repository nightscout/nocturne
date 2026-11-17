namespace Nocturne.Core.Contracts.Multitenancy;

/// <summary>
/// Names the set of readers of a tenant who see exactly the rows the current request sees, so a
/// cross-request cache keyed by it never hands one reader a result built from rows another may not
/// see. Row level security narrows database reads, not cache hits; this carries that narrowing to
/// the cache key.
/// </summary>
public interface IReaderCachePartition
{
    /// <summary>
    /// The partition, or <c>null</c> when the reader sees a narrowing no partition describes and so
    /// must neither read nor write a shared cache.
    /// </summary>
    string? Current { get; }
}
