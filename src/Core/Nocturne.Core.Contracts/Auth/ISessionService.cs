namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Orchestrates first-party session lifecycle: issuing access/refresh token
/// pairs and rotating refresh tokens with fresh claims.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Issue a new session (access + refresh token pair) for a subject.
    /// </summary>
    /// <param name="subjectId">Subject identifier.</param>
    /// <param name="context">Contextual metadata for the session.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SessionTokenPair> IssueSessionAsync(
        Guid subjectId, SessionContext context, CancellationToken ct = default);

    /// <summary>
    /// Rotate an existing refresh token, issuing a new token pair with
    /// freshly resolved roles and permissions.
    /// </summary>
    /// <param name="refreshToken">Current refresh token to rotate.</param>
    /// <param name="context">Contextual metadata for the session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>New token pair, or null if the refresh token is invalid.</returns>
    Task<SessionTokenPair?> RotateSessionAsync(
        string refreshToken, SessionContext context, CancellationToken ct = default);
}

/// <summary>
/// Contextual metadata attached to a session for audit and device tracking.
/// </summary>
public record SessionContext(
    string? OidcSessionId = null,
    string? DeviceDescription = null,
    string? IpAddress = null,
    string? UserAgent = null);

/// <summary>
/// An access/refresh token pair with the access token's lifetime.
/// </summary>
public record SessionTokenPair(
    string AccessToken, string RefreshToken, int ExpiresInSeconds);
