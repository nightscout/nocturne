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
    public async Task<GrantRevocationOutcome> RevokeAsync(
        NocturneDbContext db,
        OAuthGrantEntity grant,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        grant.RevokedAt = now;

        var refreshTokens = await db.OAuthRefreshTokens
            .Where(t => t.GrantId == grant.Id && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in refreshTokens)
        {
            token.RevokedAt = now;
        }

        var removedDevices = await db.RemoveGrantDevicesAsync(grant.Id, ct);

        await db.SaveChangesAsync(ct);

        // The cached guest session is what makes a revoked link stop resolving before its 30-second
        // TTL, so it has to be evicted on every revoke path, not only the ones that enter
        // GuestLinkService.
        _guestSessionCache.Evict(grant.TenantId, grant.Id);

        return new GrantRevocationOutcome(refreshTokens.Count, removedDevices);
    }
}

/// <summary>What a revocation changed, for the caller's audit line.</summary>
public sealed record GrantRevocationOutcome(int RevokedRefreshTokenCount, int RemovedDeviceCount);
