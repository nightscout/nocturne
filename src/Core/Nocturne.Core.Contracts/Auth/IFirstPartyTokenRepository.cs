namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Persistence boundary for first-party refresh tokens.
/// Implementations may be EF Core or in-memory (for tests).
/// </summary>
public interface IFirstPartyTokenRepository
{
    Task CreateAsync(RefreshTokenRecord record, CancellationToken ct = default);
    Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken ct = default);
    Task RevokeAsync(Guid tokenId, string reason, Guid? replacedByTokenId = null, CancellationToken ct = default);

    /// <summary>
    /// Atomically marks an active token as rotated (revokes it and links it to its
    /// replacement) and stores <paramref name="successor"/>, but only if the token is still
    /// active (not yet revoked). Returns true if this caller performed the rotation, false if a
    /// concurrent request already rotated it.
    /// The single conditional UPDATE lets exactly one of several parallel refresh requests
    /// win, which prevents the token family from forking under the concurrent requests an
    /// SSR app issues per navigation (page load, preload, proxied API and remote-function
    /// calls all carry the same refresh-token cookie). The claim and the insert commit together,
    /// so a failure cannot revoke the token without leaving its successor behind.
    /// </summary>
    Task<bool> TryRotateAsync(Guid tokenId, RefreshTokenRecord successor, CancellationToken ct = default);
    Task<int> RevokeAllForSubjectAsync(Guid subjectId, string reason, CancellationToken ct = default);
    Task<int> RevokeByOidcSessionAsync(string oidcSessionId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Revokes all active tokens of one session belonging to the subject. The session key is
    /// an OIDC session ID, or a token ID for legacy tokens issued before sessions were tagged.
    /// Subject-scoped so a client-supplied key cannot touch another subject's tokens.
    /// </summary>
    Task<int> RevokeSessionForSubjectAsync(Guid subjectId, string sessionId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Revokes all of the subject's active tokens except those in the given session.
    /// </summary>
    Task<int> RevokeOtherSessionsForSubjectAsync(Guid subjectId, string currentSessionId, string reason, CancellationToken ct = default);
    Task UpdateLastUsedAsync(string tokenHash, CancellationToken ct = default);
    Task<int> PruneExpiredAsync(DateTime cutoff, CancellationToken ct = default);
    Task<List<RefreshTokenInfo>> GetActiveSessionsAsync(Guid subjectId, CancellationToken ct = default);

    /// <summary>
    /// Whether the subject has a token live at <paramref name="now"/> in session
    /// <paramref name="sessionId"/>, keyed as <see cref="RevokeSessionForSubjectAsync"/> says, or in
    /// any session when <paramref name="sessionId"/> is null: a connection admitted on an access
    /// token minted before every chain carried a session key can only be matched to the subject.
    /// </summary>
    /// <remarks>
    /// Read pinned to the subject rather than as the scope's reader: the hub revalidation sweep
    /// runs in a scope of its own, and the pin is what admits the subject's rows there.
    /// </remarks>
    Task<bool> HasLiveSessionAsync(Guid subjectId, string? sessionId, DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Deletes every token of the subject, live or not, and returns how many. For retiring an
    /// account whose rows must not stay in the table, where <see cref="RevokeAllForSubjectAsync"/>
    /// would leave them.
    /// </summary>
    Task<int> DeleteAllForSubjectAsync(Guid subjectId, CancellationToken ct = default);
}

public record RefreshTokenRecord(
    Guid Id,
    string TokenHash,
    Guid SubjectId,
    string? OidcSessionId,
    string? DeviceDescription,
    string? IpAddress,
    string? UserAgent,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    string? RevokedReason,
    Guid? ReplacedByTokenId,
    DateTime? LastUsedAt);
