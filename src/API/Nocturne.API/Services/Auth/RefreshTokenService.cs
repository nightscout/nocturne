using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Thrown when a revoked refresh token is reused within the grace period,
/// indicating a harmless race condition between concurrent requests rather
/// than token theft. Handlers should skip cookie clearing when catching this.
/// </summary>
public class TokenRotationRaceException : Exception
{
    public TokenRotationRaceException()
        : base("Refresh token reuse within grace period — concurrent request race condition.") { }
}

/// <summary>
/// Service for creating, validating, rotating, and revoking refresh tokens stored in the database.
/// Token values are never stored in plaintext — only the SHA-256 hash is persisted.
/// Rotation on use prevents token replay attacks.
/// </summary>
/// <seealso cref="IRefreshTokenService"/>
/// <seealso cref="IJwtService"/>
/// <seealso cref="OAuthTokenService"/>
public class RefreshTokenService : IRefreshTokenService
{
    private readonly IFirstPartyTokenRepository _repository;
    private readonly IJwtService _jwtService;
    private readonly IRotationSuccessorCache _successorCache;
    private readonly JwtOptions _options;
    private readonly ILogger<RefreshTokenService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RefreshTokenService"/>.
    /// </summary>
    /// <param name="repository">Repository for refresh token persistence.</param>
    /// <param name="jwtService">Service for generating crypto-random tokens and computing token hashes.</param>
    /// <param name="successorCache">Grace-period store replaying a rotation's successor to concurrent requests.</param>
    /// <param name="options">JWT configuration options including refresh token lifetime settings.</param>
    /// <param name="logger">The logger instance.</param>
    public RefreshTokenService(
        IFirstPartyTokenRepository repository,
        IJwtService jwtService,
        IRotationSuccessorCache successorCache,
        IOptions<JwtOptions> options,
        ILogger<RefreshTokenService> logger)
    {
        _repository = repository;
        _jwtService = jwtService;
        _successorCache = successorCache;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string> CreateRefreshTokenAsync(
        Guid subjectId,
        string? oidcSessionId = null,
        string? deviceDescription = null,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var refreshToken = _jwtService.GenerateRefreshToken();
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var record = new RefreshTokenRecord(
            Id: Guid.CreateVersion7(),
            TokenHash: tokenHash,
            SubjectId: subjectId,
            OidcSessionId: oidcSessionId,
            DeviceDescription: deviceDescription,
            IpAddress: ipAddress,
            UserAgent: userAgent,
            IssuedAt: DateTime.UtcNow,
            ExpiresAt: DateTime.UtcNow.AddDays(_options.RefreshTokenLifetimeDays),
            RevokedAt: null,
            RevokedReason: null,
            ReplacedByTokenId: null,
            LastUsedAt: null);

        await _repository.CreateAsync(record);

        _logger.LogDebug("Created refresh token for subject {SubjectId}", subjectId);

        return refreshToken;
    }

    /// <inheritdoc />
    public async Task<RefreshTokenSession?> ValidateRefreshTokenAsync(string refreshToken)
    {
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var record = await _repository.FindByHashAsync(tokenHash);

        if (record == null)
        {
            _logger.LogDebug("Refresh token not found");
            return null;
        }

        if (record.RevokedAt != null)
        {
            _logger.LogWarning(
                "Attempt to use revoked refresh token {TokenId} for subject {SubjectId}",
                record.Id, record.SubjectId);
            return null;
        }

        if (record.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogDebug("Refresh token {TokenId} has expired", record.Id);
            return null;
        }

        return new RefreshTokenSession(record.SubjectId, SessionKeyOf(record));
    }

    /// <inheritdoc />
    public async Task<string?> RotateRefreshTokenAsync(
        string oldRefreshToken,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var tokenHash = _jwtService.HashRefreshToken(oldRefreshToken);

        var oldRecord = await _repository.FindByHashAsync(tokenHash);

        if (oldRecord == null || oldRecord.ExpiresAt < DateTime.UtcNow)
        {
            // Unknown or expired token — nothing to rotate.
            return null;
        }

        if (oldRecord.RevokedAt != null)
        {
            // Already revoked: decide whether this is a benign concurrent/stale-client
            // reuse or genuine token theft.
            return await HandleReuseOfRevokedTokenAsync(oldRecord);
        }

        // Token is currently active. Mint the replacement up front, then atomically claim the
        // rotation: of N concurrent requests carrying this token, only one can flip the row
        // from active to revoked. This stops the family forking into many siblings (which
        // later surface as "reuse" and trigger a whole-session revocation).
        var newRefreshToken = _jwtService.GenerateRefreshToken();
        var newTokenHash = _jwtService.HashRefreshToken(newRefreshToken);

        var newRecord = new RefreshTokenRecord(
            Id: Guid.CreateVersion7(),
            TokenHash: newTokenHash,
            SubjectId: oldRecord.SubjectId,
            // A chain issued before sessions were tagged takes its session key here, so its next
            // access token can name its session (sid) and the owner read before rotation agrees.
            OidcSessionId: SessionKeyOf(oldRecord),
            DeviceDescription: oldRecord.DeviceDescription,
            IpAddress: ipAddress ?? oldRecord.IpAddress,
            UserAgent: userAgent ?? oldRecord.UserAgent,
            IssuedAt: DateTime.UtcNow,
            ExpiresAt: DateTime.UtcNow.AddDays(_options.RefreshTokenLifetimeDays),
            RevokedAt: null,
            RevokedReason: null,
            ReplacedByTokenId: null,
            LastUsedAt: null);

        var claimed = await _repository.TryRotateAsync(oldRecord.Id, newRecord);
        if (!claimed)
        {
            // A concurrent request rotated this token between our read and our claim. Treat
            // it as a benign race: don't create a second successor (no fork) and don't revoke
            // the family. If the winner has already published its successor, hand that back so
            // this request authenticates with the same pair the client is converging on.
            var racedSuccessor = await _successorCache.GetAsync(tokenHash);
            if (racedSuccessor != null)
            {
                _logger.LogDebug(
                    "Lost refresh-token rotation race for subject {SubjectId} — replaying the winner's successor.",
                    oldRecord.SubjectId);
                return await LiveOrNullAsync(racedSuccessor);
            }

            _logger.LogDebug(
                "Lost refresh-token rotation race for subject {SubjectId} — a concurrent request rotated first.",
                oldRecord.SubjectId);
            throw new TokenRotationRaceException();
        }

        // Publish the successor for the grace window so concurrent requests still carrying
        // the old cookie receive this same token instead of failing authentication.
        await _successorCache.StoreAsync(tokenHash, newRefreshToken, RefreshTokenRotation.GracePeriod);

        _logger.LogDebug(
            "Rotated refresh token for subject {SubjectId}. Old: {OldTokenId}, New: {NewTokenId}",
            oldRecord.SubjectId, oldRecord.Id, newRecord.Id);

        return newRefreshToken;
    }

    /// <summary>
    /// Handles presentation of an already-revoked refresh token. Reuse of a rotated token
    /// within <see cref="RefreshTokenRotation.GracePeriod"/> is a concurrent-request race (the client had
    /// not yet received the rotated cookie) — when the successor is still cached, it is
    /// replayed so the request authenticates like the one that won the rotation. Beyond the
    /// grace period, reuse signals the token leaked and the
    /// token family is revoked. Tokens revoked for any other reason are simply rejected.
    /// </summary>
    private async Task<string?> HandleReuseOfRevokedTokenAsync(RefreshTokenRecord oldRecord)
    {
        // Only a rotated token (one that links to a successor) participates in reuse handling;
        // a token revoked by logout, manual revocation, or a prior family revocation is rejected.
        if (!oldRecord.ReplacedByTokenId.HasValue)
        {
            return null;
        }

        var timeSinceRevocation = DateTime.UtcNow - oldRecord.RevokedAt!.Value;
        if (timeSinceRevocation < RefreshTokenRotation.GracePeriod)
        {
            var successor = await _successorCache.GetAsync(oldRecord.TokenHash);
            if (successor != null)
            {
                _logger.LogDebug(
                    "Refresh token reuse within grace period ({Elapsed:F1}s) for subject {SubjectId} — " +
                    "concurrent request, replaying the rotation's successor.",
                    timeSinceRevocation.TotalSeconds, oldRecord.SubjectId);
                return await LiveOrNullAsync(successor);
            }

            _logger.LogDebug(
                "Refresh token reuse within grace period ({Elapsed:F1}s) for subject {SubjectId} — " +
                "concurrent request, skipping family revocation.",
                timeSinceRevocation.TotalSeconds, oldRecord.SubjectId);
            throw new TokenRotationRaceException();
        }

        // Outside the grace period — this looks like actual token theft. Revoke the affected
        // session's chain rather than every session the subject has, so one compromised (or
        // misbehaving) device doesn't sign the user out everywhere. Tokens issued before
        // sessions were tagged have no session id; fall back to a subject-wide revocation.
        if (!string.IsNullOrEmpty(oldRecord.OidcSessionId))
        {
            _logger.LogWarning(
                "Refresh token reuse detected for subject {SubjectId} ({Elapsed:F0}s after rotation). " +
                "Revoking session {OidcSessionId}.",
                oldRecord.SubjectId, timeSinceRevocation.TotalSeconds, oldRecord.OidcSessionId);

            await _repository.RevokeByOidcSessionAsync(oldRecord.OidcSessionId, "Token reuse detected");
        }
        else
        {
            _logger.LogWarning(
                "Refresh token reuse detected for subject {SubjectId} ({Elapsed:F0}s after rotation), " +
                "no session id on token — revoking all of the subject's tokens.",
                oldRecord.SubjectId, timeSinceRevocation.TotalSeconds);

            await _repository.RevokeAllForSubjectAsync(oldRecord.SubjectId, "Token reuse detected");
        }

        return null;
    }

    /// <summary>
    /// A cached successor outlives its row's revocation: logout, a device revoke or reuse
    /// detection can revoke it inside the grace period, and replaying it then would sign the
    /// caller back into a revoked session.
    /// </summary>
    private async Task<string?> LiveOrNullAsync(string successor)
    {
        var record = await _repository.FindByHashAsync(_jwtService.HashRefreshToken(successor));
        return record is { RevokedAt: null } && record.ExpiresAt >= DateTime.UtcNow ? successor : null;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeRefreshTokenAsync(string refreshToken, string reason)
    {
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var record = await _repository.FindByHashAsync(tokenHash);

        if (record == null)
        {
            return false;
        }

        if (record.RevokedAt != null)
        {
            return true; // Already revoked
        }

        await _repository.RevokeAsync(record.Id, reason);

        _logger.LogInformation(
            "Revoked refresh token {TokenId} for subject {SubjectId}. Reason: {Reason}",
            record.Id, record.SubjectId, reason);

        return true;
    }

    /// <inheritdoc />
    public async Task<List<RefreshTokenInfo>> GetActiveSessionsForSubjectAsync(Guid subjectId)
    {
        var tokens = await _repository.GetActiveSessionsAsync(subjectId);

        // The rotation grace window means a session briefly has several live tokens. Group per
        // session (keyed as SessionKeyOf keys it) so each login/device appears once.
        return tokens
            .GroupBy(t => t.OidcSessionId ?? t.Id.ToString())
            .Select(g =>
            {
                var newest = g.OrderByDescending(t => t.IssuedAt).First();
                return new RefreshTokenInfo
                {
                    Id = newest.Id,
                    OidcSessionId = g.Key,
                    DeviceDescription = newest.DeviceDescription,
                    IpAddress = newest.IpAddress,
                    IssuedAt = g.Min(t => t.IssuedAt),
                    LastUsedAt = g.Max(t => t.LastUsedAt ?? t.IssuedAt),
                    ExpiresAt = g.Max(t => t.ExpiresAt),
                    IsCurrent = false,
                };
            })
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<RefreshTokenSession?> FindOwnerAsync(string refreshToken)
    {
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        var record = await _repository.FindByHashAsync(tokenHash);
        if (record == null)
        {
            return null;
        }

        return new RefreshTokenSession(record.SubjectId, SessionKeyOf(record));
    }

    /// <summary>
    /// The session a token's chain belongs to: its OIDC session id, or for a legacy token issued
    /// before sessions were tagged, the token's own id. The key
    /// <see cref="IFirstPartyTokenRepository.RevokeSessionForSubjectAsync"/> matches.
    /// </summary>
    private static string SessionKeyOf(RefreshTokenRecord record) =>
        record.OidcSessionId ?? record.Id.ToString();

    /// <inheritdoc />
    public async Task<int> RevokeSessionForSubjectAsync(Guid subjectId, string sessionId, string reason)
    {
        var count = await _repository.RevokeSessionForSubjectAsync(subjectId, sessionId, reason);

        _logger.LogInformation(
            "Revoked {Count} refresh tokens for session {SessionId} of subject {SubjectId}. Reason: {Reason}",
            count, sessionId, subjectId, reason);

        return count;
    }

    /// <inheritdoc />
    public async Task<int> RevokeOtherSessionsForSubjectAsync(Guid subjectId, string currentSessionId, string reason)
    {
        var count = await _repository.RevokeOtherSessionsForSubjectAsync(subjectId, currentSessionId, reason);

        _logger.LogInformation(
            "Revoked {Count} refresh tokens across other sessions of subject {SubjectId}. Reason: {Reason}",
            count, subjectId, reason);

        return count;
    }

    /// <inheritdoc />
    public async Task UpdateLastUsedAsync(string refreshToken)
    {
        var tokenHash = _jwtService.HashRefreshToken(refreshToken);

        await _repository.UpdateLastUsedAsync(tokenHash);
    }

    /// <inheritdoc />
    public async Task<int> PruneExpiredRefreshTokensAsync(DateTime? olderThan = null)
    {
        var cutoffDate = olderThan ?? DateTime.UtcNow.AddDays(-30);

        var count = await _repository.PruneExpiredAsync(cutoffDate);

        if (count > 0)
        {
            _logger.LogInformation("Pruned {Count} expired/old refresh tokens", count);
        }

        return count;
    }
}
