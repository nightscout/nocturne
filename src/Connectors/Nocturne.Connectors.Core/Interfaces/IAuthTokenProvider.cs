namespace Nocturne.Connectors.Core.Interfaces;

/// <summary>
///     Interface for centralized authentication token lifecycle management.
///     Provides a consistent pattern for token caching, expiry checking, and thread-safe refresh.
/// </summary>
public interface IAuthTokenProvider
{
    /// <summary>
    ///     Gets whether the current token is expired or missing.
    /// </summary>
    bool IsTokenExpired { get; }

    /// <summary>
    ///     Gets the expiration time of the current token, if available.
    /// </summary>
    DateTime? TokenExpiresAt { get; }

    /// <summary>
    ///     Gets a valid authentication token, refreshing if expired.
    ///     This method is thread-safe and will only perform one token refresh at a time.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A valid token, or null if authentication fails</returns>
    Task<string?> GetValidTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Invalidates the current token, forcing a refresh on the next call to GetValidTokenAsync.
    ///     Call this when receiving a 401 Unauthorized response.
    /// </summary>
    void InvalidateToken();
}