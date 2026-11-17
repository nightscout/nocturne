namespace Nocturne.API.Services.Caching;

/// <summary>
/// A cache factory's value and how long it is cached. A <c>null</c> <see cref="Ttl"/> returns
/// the value uncached, so the next read runs a factory again. A <c>null</c> <see cref="Value"/> is never
/// cached; <see cref="ITenantCache.GetOrSetUnlessNullAsync{T}(string, Func{Task{T}}, TimeSpan, CancellationToken)"/> returns it, and the other members throw.
/// </summary>
public readonly record struct CacheResult<T>(T? Value, TimeSpan? Ttl) where T : class;
