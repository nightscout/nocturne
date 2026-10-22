using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Applies every consequence of revoking a grant in one place: the <c>RevokedAt</c> stamp, the
/// refresh tokens issued under it, its registered client devices, and its cached guest session.
/// </summary>
/// <remarks>
/// Runs on the caller's context, never one it resolves itself, so a platform-admin caller's
/// tenant pin decides which grant the consequences apply to.
/// </remarks>
public sealed class GrantRevocationService
{
    private readonly GuestSessionCacheService _guestSessionCache;

    public GrantRevocationService(GuestSessionCacheService guestSessionCache)
    {
        _guestSessionCache = guestSessionCache;
    }

    /// <summary>
    /// Revokes <paramref name="grant"/>, applies every consequence, then saves.
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
    /// Revokes every one of <paramref name="grants"/>, applies every consequence, then saves. The
    /// save also commits whatever else the caller has staged on <paramref name="db"/>, so a change
    /// that must not outlive the grants, or be outlived by them, lands with them.
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

        var refreshTokens = await db.OAuthRefreshTokens
            .Where(t => grantIds.Contains(t.GrantId) && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in refreshTokens)
        {
            token.RevokedAt = now;
        }

        var removedDevices = 0;
        foreach (var grantId in grantIds)
        {
            removedDevices += await db.RemoveGrantDevicesAsync(grantId, ct);
        }

        await db.SaveChangesAsync(ct);

        // The cached guest session is what makes a revoked link stop resolving before its 30-second
        // TTL, so it has to be evicted on every revoke path, not only the ones that enter
        // GuestLinkService.
        foreach (var grant in grants)
        {
            _guestSessionCache.Evict(grant.TenantId, grant.Id);
        }

        return new GrantRevocationOutcome(refreshTokens.Count, removedDevices);
    }
}

/// <summary>What a revocation changed, for the caller's audit line.</summary>
public sealed record GrantRevocationOutcome(int RevokedRefreshTokenCount, int RemovedDeviceCount);
