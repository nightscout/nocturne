using Nocturne.API.Services.ClientDevices;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Applies every consequence of revoking a grant in one place: the <c>RevokedAt</c> stamp, the
/// refresh tokens issued under it, its registered client devices, and its cached guest session.
/// </summary>
/// <remarks>
/// A writer on the caller's <see cref="DbUnitOfWork"/>, never a context it resolves itself, so a
/// platform-admin caller's tenant pin decides which grant the consequences apply to. It stages
/// the grants, their tokens and their devices and saves nothing: the owner commits the unit, so
/// a token cannot outlive its grant or die before it, and whatever else the owner staged lands
/// with them. The guest session cache is evicted by the owner after the commit, through
/// <see cref="EvictSessions"/>, so a session is never dropped for a revoke that did not land.
/// </remarks>
public sealed class GrantRevocationService
{
    private readonly GuestSessionCacheService _guestSessionCache;

    public GrantRevocationService(GuestSessionCacheService guestSessionCache)
    {
        _guestSessionCache = guestSessionCache;
    }

    /// <summary>
    /// Stages the revocation of <paramref name="grant"/> with every consequence, as
    /// <see cref="StageAsync(DbUnitOfWork, IReadOnlyCollection{OAuthGrantRecord}, CancellationToken)"/> does.
    /// </summary>
    public Task<GrantRevocationOutcome> StageAsync(
        DbUnitOfWork unit,
        OAuthGrantRecord grant,
        CancellationToken ct = default) =>
        StageAsync(unit, [grant], ct);

    /// <summary>
    /// Stages on <paramref name="unit"/> the revocation of every one of <paramref name="grants"/>
    /// with their refresh tokens and device registrations. The owner commits the unit, then calls
    /// <see cref="EvictSessions"/>.
    /// </summary>
    /// <param name="unit">The caller's unit of work, on the grants' tenant.</param>
    /// <param name="grants">The grants to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<GrantRevocationOutcome> StageAsync(
        DbUnitOfWork unit,
        IReadOnlyCollection<OAuthGrantRecord> grants,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var grantIds = grants.Select(g => g.Id).ToList();

        await new OAuthGrantRepository(unit).RevokeAsync(grantIds, now, ct);

        var removedDevices = 0;
        foreach (var grantId in grantIds)
        {
            removedDevices += await unit.Context.RemoveGrantDevicesAsync(grantId, ct);
        }

        var revokedTokens = await new OAuthTokenRepository(unit)
            .RevokeRefreshTokensOfGrantsAsync(grantIds, now, ct);

        return new GrantRevocationOutcome(revokedTokens, removedDevices);
    }

    /// <summary>
    /// Drops the cached guest sessions of <paramref name="grants"/>, once their revocation has
    /// committed. The cached session is what makes a revoked link stop resolving before its
    /// 30-second TTL, so it is evicted on every revoke path, not only the ones that enter
    /// <see cref="GuestLinkService"/>.
    /// </summary>
    public void EvictSessions(IEnumerable<OAuthGrantRecord> grants)
    {
        foreach (var grant in grants)
        {
            _guestSessionCache.Evict(grant.TenantId, grant.Id);
        }
    }
}

/// <summary>What a revocation changed, for the caller's audit line.</summary>
public sealed record GrantRevocationOutcome(int RevokedRefreshTokenCount, int RemovedDeviceCount);
