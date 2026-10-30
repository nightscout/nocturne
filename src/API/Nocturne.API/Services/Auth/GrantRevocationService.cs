using Nocturne.API.Services.ClientDevices;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Applies every consequence of revoking a grant in one place: the <c>RevokedAt</c> stamp, the
/// refresh tokens issued under it, its registered client devices, and its cached guest session.
/// </summary>
/// <remarks>
/// Runs on the caller's <see cref="DbUnitOfWork"/>, never a context it resolves itself, so a
/// platform-admin caller's tenant pin decides which grant the consequences apply to. The grants,
/// their tokens and their devices are staged on the unit and committed together in
/// <see cref="RevokeAllAsync"/>, which is the unit's commit point: a token cannot outlive its grant
/// or die before it, and whatever else the caller staged lands with them.
/// </remarks>
public sealed class GrantRevocationService
{
    private readonly GuestSessionCacheService _guestSessionCache;

    public GrantRevocationService(GuestSessionCacheService guestSessionCache)
    {
        _guestSessionCache = guestSessionCache;
    }

    /// <summary>
    /// Revokes <paramref name="grant"/> and applies every consequence, as
    /// <see cref="RevokeAllAsync"/> does.
    /// </summary>
    /// <param name="unit">The caller's unit of work, on the grant's tenant.</param>
    /// <param name="grant">The grant to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    public Task<GrantRevocationOutcome> RevokeAsync(
        DbUnitOfWork unit,
        OAuthGrantRecord grant,
        CancellationToken ct = default) =>
        RevokeAllAsync(unit, [grant], ct);

    /// <summary>
    /// Revokes every one of <paramref name="grants"/> with their refresh tokens and device
    /// registrations, commits <paramref name="unit"/>, then evicts their guest sessions. The commit
    /// also lands whatever else the caller has staged on the unit, so a change that must not outlive
    /// the grants, or be outlived by them, lands with them.
    /// </summary>
    /// <param name="unit">The caller's unit of work, on the grants' tenant.</param>
    /// <param name="grants">The grants to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<GrantRevocationOutcome> RevokeAllAsync(
        DbUnitOfWork unit,
        IReadOnlyCollection<OAuthGrantRecord> grants,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var grantIds = grants.Select(g => g.Id).ToList();

        var outcome = await unit.CommitAsync(async token =>
        {
            await new OAuthGrantRepository(unit).RevokeAsync(grantIds, now, token);

            var removedDevices = 0;
            foreach (var grantId in grantIds)
            {
                removedDevices += await unit.Context.RemoveGrantDevicesAsync(grantId, token);
            }

            var revokedTokens = await new OAuthTokenRepository(unit)
                .RevokeRefreshTokensOfGrantsAsync(grantIds, now, token);

            await unit.Context.SaveChangesAsync(token);
            return new GrantRevocationOutcome(revokedTokens, removedDevices);
        }, ct: ct);

        // The cached guest session is what makes a revoked link stop resolving before its 30-second
        // TTL, so it has to be evicted on every revoke path, not only the ones that enter
        // GuestLinkService.
        foreach (var grant in grants)
        {
            _guestSessionCache.Evict(grant.TenantId, grant.Id);
        }

        return outcome;
    }
}

/// <summary>What a revocation changed, for the caller's audit line.</summary>
public sealed record GrantRevocationOutcome(int RevokedRefreshTokenCount, int RemovedDeviceCount);
