using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Cache-backed store for tracking revoked OAuth access tokens by their JWT identifier (<c>jti</c>).
/// Cache entries automatically expire when the token's natural lifetime would have elapsed,
/// preventing unbounded growth.
/// </summary>
/// <seealso cref="IOAuthTokenRevocationCache"/>
public class OAuthTokenRevocationCache : IOAuthTokenRevocationCache
{
    private readonly ICacheService _cache;
    private readonly ILogger<OAuthTokenRevocationCache> _logger;

    private const string KeyPrefix = "oauth:revoked:";

    /// <summary>
    /// Initialises a new <see cref="OAuthTokenRevocationCache"/>.
    /// </summary>
    /// <param name="cache">Distributed or in-process cache used for revocation markers.</param>
    /// <param name="logger">Logger instance.</param>
    public OAuthTokenRevocationCache(ICacheService cache, ILogger<OAuthTokenRevocationCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string jti, TimeSpan remainingLifetime, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jti))
            return;

        // Only cache if the token hasn't already expired
        if (remainingLifetime <= TimeSpan.Zero)
            return;

        var key = $"{KeyPrefix}{jti}";
        await _cache.SetAsync(key, new RevokedTokenMarker { RevokedAt = DateTime.UtcNow }, remainingLifetime, ct);

        _logger.LogDebug("Marked token {Jti} as revoked, cache TTL {TTL}", jti, remainingLifetime);
    }

    /// <inheritdoc />
    public async Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jti))
            return false;

        var key = $"{KeyPrefix}{jti}";
        return await _cache.ExistsAsync(key, ct);
    }
}

/// <summary>
/// Marker class stored in cache to indicate a revoked token.
/// </summary>
internal class RevokedTokenMarker
{
    public DateTime RevokedAt { get; set; }
}
