using Nocturne.API.Services.ClientDevices;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Applies every consequence of revoking a grant in one place: the <c>RevokedAt</c> stamp, the
/// refresh tokens issued under it, its registered client devices, and its cached guest session.
/// </summary>
/// <remarks>
/// Runs on the caller's context, never one it resolves itself, so a platform-admin caller's
/// tenant pin decides which grant the consequences apply to; the refresh tokens are revoked
/// through <see cref="IOAuthTokenRepository"/> pinned to each grant's tenant for the same reason.
/// The grant and its devices save on the caller's context first; the tokens are then revoked in a
/// second context, by <see cref="IOAuthTokenRepository.RevokeRefreshTokensOfRevokedGrantsAsync"/>,
/// which only reaches tokens of a grant already revoked. The two steps are not one transaction,
/// which is safe because the refresh path refuses a revoked grant before any grace or rotation
/// step: a token the second step never reached is dead anyway. One transaction needs the callers
/// to hand over a unit of work (#3102).
/// </remarks>
public sealed class GrantRevocationService
{
    private readonly GuestSessionCacheService _guestSessionCache;
    private readonly IOAuthTokenRepository _tokens;

    public GrantRevocationService(GuestSessionCacheService guestSessionCache, IOAuthTokenRepository tokens)
    {
        _guestSessionCache = guestSessionCache;
        _tokens = tokens;
    }

    /// <summary>
    /// Revokes <paramref name="grant"/> and applies every consequence, as
    /// <see cref="RevokeAllAsync"/> does.
    /// </summary>
    /// <param name="db">The caller's tenant-scoped context, tracking <paramref name="grant"/>.</param>
    /// <param name="grant">The grant to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    public Task<GrantRevocationOutcome> RevokeAsync(
        NocturneDbContext db,
        OAuthGrantEntity grant,
        CancellationToken ct = default) =>
        RevokeAllAsync(db, [grant], ct);

    /// <summary>
    /// Revokes every one of <paramref name="grants"/>, saves the grants and their device removals
    /// on <paramref name="db"/>, then revokes their refresh tokens and evicts their guest sessions.
    /// The save also commits whatever else the caller has staged on <paramref name="db"/>, so a
    /// change that must not outlive the grants, or be outlived by them, lands with them.
    /// </summary>
    /// <param name="db">The caller's tenant-scoped context, tracking <paramref name="grants"/>.</param>
    /// <param name="grants">The grants to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<GrantRevocationOutcome> RevokeAllAsync(
        NocturneDbContext db,
        IReadOnlyCollection<OAuthGrantEntity> grants,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var grantIds = grants.Select(g => g.Id).ToList();

        foreach (var grant in grants)
        {
            grant.RevokedAt = now;
        }

        var removedDevices = 0;
        foreach (var grantId in grantIds)
        {
            removedDevices += await db.RemoveGrantDevicesAsync(grantId, ct);
        }

        await db.SaveChangesAsync(ct);

        var revokedTokens = 0;
        foreach (var tenant in grants.GroupBy(g => g.TenantId))
        {
            revokedTokens += await _tokens.RevokeRefreshTokensOfRevokedGrantsAsync(
                tenant.Key, tenant.Select(g => g.Id).ToList(), now, ct);
        }

        // The cached guest session is what makes a revoked link stop resolving before its 30-second
        // TTL, so it has to be evicted on every revoke path, not only the ones that enter
        // GuestLinkService.
        foreach (var grant in grants)
        {
            _guestSessionCache.Evict(grant.TenantId, grant.Id);
        }

        return new GrantRevocationOutcome(revokedTokens, removedDevices);
    }
}

/// <summary>What a revocation changed, for the caller's audit line.</summary>
public sealed record GrantRevocationOutcome(int RevokedRefreshTokenCount, int RemovedDeviceCount);
