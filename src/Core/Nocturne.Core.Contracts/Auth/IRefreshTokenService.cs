namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for managing refresh tokens (stored in database)
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Create and store a new refresh token for a subject
    /// </summary>
    /// <param name="oidcSessionId">OIDC session ID (for logout)</param>
    /// <param name="deviceDescription">Device description for session management</param>
    /// <param name="ipAddress">Client IP address</param>
    /// <returns>Plain refresh token string (only returned once)</returns>
    Task<string> CreateRefreshTokenAsync(
        Guid subjectId,
        string? oidcSessionId = null,
        string? deviceDescription = null,
        string? ipAddress = null,
        string? userAgent = null);

    /// <summary>
    /// Validate a refresh token and return subject ID if valid
    /// </summary>
    /// <param name="refreshToken">Plain refresh token string</param>
    /// <returns>Subject ID if valid, null if invalid/expired/revoked</returns>
    Task<Guid?> ValidateRefreshTokenAsync(string refreshToken);

    /// <summary>
    /// Rotate a refresh token - invalidate old one and create new one
    /// Used for refresh token rotation security pattern
    /// </summary>
    /// <param name="ipAddress">Client IP address</param>
    /// <returns>New refresh token if rotation succeeded, null if old token invalid</returns>
    Task<string?> RotateRefreshTokenAsync(
        string oldRefreshToken,
        string? ipAddress = null,
        string? userAgent = null);

    /// <param name="refreshToken">Plain refresh token string</param>
    /// <param name="reason">Reason for revocation</param>
    /// <returns>True if revoked, false if not found</returns>
    Task<bool> RevokeRefreshTokenAsync(string refreshToken, string reason);

    /// <summary>
    /// Get all active sessions for a subject (for session management UI).
    /// Tokens are grouped by OIDC session ID — one entry per login/device,
    /// since a rotation chain shares a single session ID.
    /// </summary>
    Task<List<RefreshTokenInfo>> GetActiveSessionsForSubjectAsync(Guid subjectId);

    /// <summary>
    /// Resolve the session key (OIDC session ID, or token ID for legacy tokens
    /// issued before sessions were tagged) for a presented refresh token.
    /// </summary>
    /// <param name="refreshToken">Plain refresh token string</param>
    /// <returns>Session key, or null if the token is unknown</returns>
    Task<string?> GetSessionIdForTokenAsync(string refreshToken);

    /// <summary>
    /// Revoke a single session belonging to a subject. The subject scope makes
    /// this safe to call with a client-supplied session ID.
    /// </summary>
    /// <param name="subjectId">Subject identifier (ownership guard)</param>
    /// <param name="sessionId">Session key as returned by <see cref="GetActiveSessionsForSubjectAsync"/></param>
    /// <param name="reason">Reason for revocation</param>
    /// <returns>Number of tokens revoked</returns>
    Task<int> RevokeSessionForSubjectAsync(Guid subjectId, string sessionId, string reason);

    /// <summary>
    /// Revoke every session of a subject except the given one (log out everywhere else).
    /// </summary>
    /// <param name="currentSessionId">Session key to keep</param>
    /// <param name="reason">Reason for revocation</param>
    /// <returns>Number of tokens revoked</returns>
    Task<int> RevokeOtherSessionsForSubjectAsync(Guid subjectId, string currentSessionId, string reason);

    /// <param name="refreshToken">Plain refresh token string</param>
    Task UpdateLastUsedAsync(string refreshToken);

    /// <summary>
    /// Delete expired refresh tokens from the database
    /// </summary>
    /// <param name="olderThan">Delete tokens that expired before this date</param>
    /// <returns>Number of tokens deleted</returns>
    Task<int> PruneExpiredRefreshTokensAsync(DateTime? olderThan = null);
}

/// <summary>
/// Refresh token information for session management
/// </summary>
public class RefreshTokenInfo
{
    /// <summary>
    /// Token ID (not the token itself)
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Session key: the OIDC session ID shared by the token's rotation chain,
    /// or the token ID for legacy tokens issued before sessions were tagged.
    /// </summary>
    public string? OidcSessionId { get; set; }

    public string? DeviceDescription { get; set; }

    public string? IpAddress { get; set; }

    public DateTime IssuedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public bool IsCurrent { get; set; }
}
