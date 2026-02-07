namespace Nocturne.Core.Contracts;

/// <summary>
/// Cache for tracking revoked OAuth access tokens.
/// Since access tokens are stateless JWTs, we need a blocklist to handle
/// revocation before natural expiry. Uses in-memory cache with TTL matching
/// the token's remaining lifetime.
/// </summary>
public interface IOAuthTokenRevocationCache
{
    /// <summary>
    /// Mark a token (by JTI claim) as revoked.
    /// The cache entry automatically expires when the token would have expired.
    /// </summary>
    /// <param name="jti">The JWT ID (jti claim) of the token</param>
    /// <param name="remainingLifetime">Time until the token's natural expiry</param>
    /// <param name="ct">Cancellation token</param>
    Task RevokeAsync(string jti, TimeSpan remainingLifetime, CancellationToken ct = default);

    /// <summary>
    /// Check if a token (by JTI claim) has been revoked.
    /// </summary>
    /// <param name="jti">The JWT ID (jti claim) to check</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if the token has been revoked</returns>
    Task<bool> IsRevokedAsync(string jti, CancellationToken ct = default);
}
