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
    /// <param name="context">Contextual metadata for the session.</param>
    Task<SessionTokenPair> IssueSessionAsync(
        Guid subjectId, SessionContext context, CancellationToken ct = default);

    /// <summary>
    /// Rotate an existing refresh token, issuing a new token pair with
    /// freshly resolved roles and permissions. Everything the pair needs is read before the
    /// rotation commits, so a failed read leaves the presented token valid.
    /// </summary>
    /// <param name="context">Contextual metadata for the session.</param>
    /// <returns>New token pair, or null if the refresh token is invalid.</returns>
    Task<SessionTokenPair?> RotateSessionAsync(
        string refreshToken, SessionContext context, CancellationToken ct = default);

    /// <summary>
    /// Read the claims a refresh of <paramref name="refreshToken"/> mints for its subject. A missing
    /// or inactive subject is refused and the token revoked, so a rotating and a non-rotating
    /// refresh agree on who may refresh.
    /// </summary>
    /// <returns>The subject's claims, or null when the subject may not refresh.</returns>
    Task<SessionClaims?> ReadRefreshClaimsAsync(
        string refreshToken, Guid subjectId, CancellationToken ct = default);
}

/// <summary>
/// The subject, roles and permissions an access token is minted from.
/// </summary>
public record SessionClaims(SubjectInfo Subject, List<string> Roles, List<string> Permissions);

/// <summary>
/// Contextual metadata attached to a session for audit and device tracking.
/// </summary>
public record SessionContext(
    string? OidcSessionId = null,
    string? DeviceDescription = null,
    string? IpAddress = null,
    string? UserAgent = null);

/// <summary>
/// An access/refresh token pair with the access token's lifetime and the subject it was issued to.
/// </summary>
public record SessionTokenPair(
    string AccessToken, string RefreshToken, int ExpiresInSeconds, Guid SubjectId);
