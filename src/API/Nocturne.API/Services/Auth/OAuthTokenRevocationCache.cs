using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Cache.Abstractions;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// In-memory cache for tracking revoked OAuth access tokens.
/// Cache entries automatically expire when the token would have expired naturally.
/// </summary>
public class OAuthTokenRevocationCache : IOAuthTokenRevocationCache
{
    private readonly ICacheService _cache;
    private readonly ILogger<OAuthTokenRevocationCache> _logger;

    private const string KeyPrefix = "oauth:revoked:";

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
