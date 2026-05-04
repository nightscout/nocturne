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
    private readonly JwtOptions _options;
    private readonly ILogger<RefreshTokenService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RefreshTokenService"/>.
    /// </summary>
    /// <param name="repository">Repository for refresh token persistence.</param>
    /// <param name="jwtService">Service for generating crypto-random tokens and computing token hashes.</param>
    /// <param name="options">JWT configuration options including refresh token lifetime settings.</param>
    /// <param name="logger">The logger instance.</param>
    public RefreshTokenService(
        IFirstPartyTokenRepository repository,
        IJwtService jwtService,
        IOptions<JwtOptions> options,
        ILogger<RefreshTokenService> logger)
    {
        _repository = repository;
        _jwtService = jwtService;
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
    public async Task<Guid?> ValidateRefreshTokenAsync(string refreshToken)
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

        return record.SubjectId;
    }

    /// <inheritdoc />
    public async Task<string?> RotateRefreshTokenAsync(
        string oldRefreshToken,
        string? ipAddress = null,
        string? userAgent = null)
    {
        var tokenHash = _jwtService.HashRefreshToken(oldRefreshToken);

        var oldRecord = await _repository.FindByHashAsync(tokenHash);

        if (oldRecord == null || oldRecord.RevokedAt != null || oldRecord.ExpiresAt < DateTime.UtcNow)
        {
            if (oldRecord != null && oldRecord.RevokedAt != null && oldRecord.ReplacedByTokenId.HasValue)
            {
                // Check if this is a race condition (concurrent requests using the
                // same token shortly after rotation) vs actual token theft.
                // A 30-second grace period prevents nuking all tokens when parallel
                // requests (SSR + preload, two tabs, page load + API call) both
                // attempt to refresh with the same token.
                var timeSinceRevocation = DateTime.UtcNow - oldRecord.RevokedAt.Value;
                if (timeSinceRevocation < TimeSpan.FromSeconds(30))
                {
                    _logger.LogDebug(
                        "Refresh token reuse within grace period ({Elapsed:F1}s) for subject {SubjectId}. " +
                        "Likely a concurrent request — skipping family revocation.",
                        timeSinceRevocation.TotalSeconds, oldRecord.SubjectId);
                    throw new TokenRotationRaceException();
                }

                // Outside grace period — this looks like actual token theft
                _logger.LogWarning(
                    "Refresh token reuse detected for subject {SubjectId}. Revoking all tokens in the family.",
                    oldRecord.SubjectId);

                await _repository.RevokeAllForSubjectAsync(oldRecord.SubjectId, "Token reuse detected");
            }
            return null;
        }

        // Create new refresh token
        var newRefreshToken = _jwtService.GenerateRefreshToken();
        var newTokenHash = _jwtService.HashRefreshToken(newRefreshToken);

        var newRecord = new RefreshTokenRecord(
            Id: Guid.CreateVersion7(),
            TokenHash: newTokenHash,
            SubjectId: oldRecord.SubjectId,
            OidcSessionId: oldRecord.OidcSessionId,
            DeviceDescription: oldRecord.DeviceDescription,
            IpAddress: ipAddress ?? oldRecord.IpAddress,
            UserAgent: userAgent ?? oldRecord.UserAgent,
            IssuedAt: DateTime.UtcNow,
            ExpiresAt: DateTime.UtcNow.AddDays(_options.RefreshTokenLifetimeDays),
            RevokedAt: null,
            RevokedReason: null,
            ReplacedByTokenId: null,
            LastUsedAt: null);

        // Revoke old token and link to new one
        await _repository.RevokeAsync(oldRecord.Id, "Rotated", newRecord.Id);

        await _repository.CreateAsync(newRecord);

        _logger.LogDebug(
            "Rotated refresh token for subject {SubjectId}. Old: {OldTokenId}, New: {NewTokenId}",
            oldRecord.SubjectId, oldRecord.Id, newRecord.Id);

        return newRefreshToken;
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
    public async Task<int> RevokeAllRefreshTokensForSubjectAsync(Guid subjectId, string reason)
    {
        var count = await _repository.RevokeAllForSubjectAsync(subjectId, reason);

        _logger.LogInformation(
            "Revoked {Count} refresh tokens for subject {SubjectId}. Reason: {Reason}",
            count, subjectId, reason);

        return count;
    }

    /// <inheritdoc />
    public async Task<int> RevokeRefreshTokensByOidcSessionAsync(string oidcSessionId, string reason)
    {
        var count = await _repository.RevokeByOidcSessionAsync(oidcSessionId, reason);

        _logger.LogInformation(
            "Revoked {Count} refresh tokens for OIDC session {SessionId}. Reason: {Reason}",
            count, oidcSessionId, reason);

        return count;
    }

    /// <inheritdoc />
    public async Task<List<RefreshTokenInfo>> GetActiveSessionsForSubjectAsync(Guid subjectId)
    {
        return await _repository.GetActiveSessionsAsync(subjectId);
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
