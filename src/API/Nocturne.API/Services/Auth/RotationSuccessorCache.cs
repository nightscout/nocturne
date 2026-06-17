using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Cache-backed store mapping a rotated refresh token's hash to its successor token.
/// Entries expire after the rotation grace period, so a replayed token only resolves
/// while the reuse is plausibly a concurrent-request race rather than theft. Keys are
/// SHA-256 hashes of the predecessor token, so the cache key itself reveals nothing.
/// </summary>
/// <seealso cref="IRotationSuccessorCache"/>
/// <seealso cref="RefreshTokenService"/>
public class RotationSuccessorCache : IRotationSuccessorCache
{
    private readonly ICacheService _cache;

    private const string KeyPrefix = "auth:rotation-successor:";

    /// <summary>
    /// Initialises a new <see cref="RotationSuccessorCache"/>.
    /// </summary>
    /// <param name="cache">Distributed or in-process cache used for successor entries.</param>
    public RotationSuccessorCache(ICacheService cache)
    {
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task StoreAsync(string oldTokenHash, string successorToken, TimeSpan ttl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(oldTokenHash) || ttl <= TimeSpan.Zero)
            return;

        await _cache.SetAsync($"{KeyPrefix}{oldTokenHash}", successorToken, ttl, ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string oldTokenHash, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(oldTokenHash))
            return null;

        return await _cache.GetAsync<string>($"{KeyPrefix}{oldTokenHash}", ct);
    }
}
