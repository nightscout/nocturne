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
    Task<int> RevokeAllForSubjectAsync(Guid subjectId, string reason, CancellationToken ct = default);
    Task<int> RevokeByOidcSessionAsync(string oidcSessionId, string reason, CancellationToken ct = default);
    Task UpdateLastUsedAsync(string tokenHash, CancellationToken ct = default);
    Task<int> PruneExpiredAsync(DateTime cutoff, CancellationToken ct = default);
    Task<List<RefreshTokenInfo>> GetActiveSessionsAsync(Guid subjectId, CancellationToken ct = default);
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
