namespace Nocturne.Core.Contracts.V4;

/// <summary>
/// Told, after every committed write of a record type, which types were written, so the legacy
/// read caches those types are served through stop serving what the write changed. Whatever the
/// <see cref="WriteOrigin"/>, and whether or not the change counted as material, since a change the
/// material gate does not count (a correlation id) still moves what a legacy read assembles.
/// </summary>
/// <remarks>
/// Takes no cancellation token: the write has committed, so the clear must run even when the
/// caller has been cancelled. Failures are logged, never thrown into the write.
/// </remarks>
public interface ILegacyReadCacheInvalidation
{
    /// <param name="models">The model types written; a type no legacy read serves is ignored.</param>
    Task WrittenAsync(IReadOnlyCollection<Type> models);
}
