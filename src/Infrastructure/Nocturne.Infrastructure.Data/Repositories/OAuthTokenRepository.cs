using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IOAuthTokenRepository"/> over <see cref="NocturneDbContext"/>, one context per call,
/// or the caller's <see cref="DbUnitOfWork"/> when constructed over one. A claim is an
/// <c>ExecuteUpdate</c> whose predicate is the state it claims from, so the row's lock, not the
/// change tracker, decides the winner.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds: the hosted cleanup sweep is a singleton, and
/// resolving the repository from the root gives it the root factory, which reads as no one and is
/// pinned per tenant on the sweep's call.
/// </remarks>
public class OAuthTokenRepository : IOAuthTokenRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public OAuthTokenRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns and saves. A
    /// claim is still one statement, run inside the unit's transaction.
    /// </summary>
    public OAuthTokenRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private async ValueTask<NocturneDbContext> OpenForTenantAsync(Guid tenantId, CancellationToken ct) =>
        await (_contexts ?? throw new InvalidOperationException(
                "A repository over a unit of work runs on the unit's tenant and cannot pin another."))
            .CreateForTenantAsync(tenantId, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    /// <inheritdoc/>
    public virtual async Task StoreAuthorizationCodeAsync(NewAuthorizationCode code, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCodeEntity
        {
            ClientEntityId = code.ClientEntityId,
            SubjectId = code.SubjectId,
            CodeHash = code.CodeHash,
            Scopes = code.Scopes.ToList(),
            RedirectUri = code.RedirectUri,
            CodeChallenge = code.CodeChallenge,
            ExpiresAt = code.ExpiresAt,
            LimitTo24Hours = code.LimitTo24Hours,
        });
        await SaveAsync(db, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryClaimAuthorizationCodeAsync(string codeHash, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var claimed = await lease.Context.OAuthAuthorizationCodes
            .Where(c => c.CodeHash == codeHash && c.RedeemedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RedeemedAt, now), ct);
        return claimed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<AuthorizationCodeRecord?> FindAuthorizationCodeAsync(string codeHash, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var code = await lease.Context.OAuthAuthorizationCodes
            .AsNoTracking()
            .Include(c => c.Client)
            .FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);
        return code is null ? null : new AuthorizationCodeRecord(
            code.Id,
            code.ClientEntityId,
            code.Client?.ClientId,
            code.SubjectId,
            code.Scopes,
            code.RedirectUri,
            code.CodeChallenge,
            code.ExpiresAt,
            code.RedeemedAt,
            code.LimitTo24Hours);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> StoreRefreshTokenAsync(NewOAuthRefreshToken token, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = new OAuthRefreshTokenEntity
        {
            TenantId = token.TenantId,
            GrantId = token.GrantId,
            TokenHash = token.TokenHash,
            ExpiresAt = token.ExpiresAt,
        };
        db.OAuthRefreshTokens.Add(entity);
        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthRefreshTokenRecord?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await TokensWithGrant(lease.Context).FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthRefreshTokenRecord?> GetRefreshTokenAsync(Guid tokenId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await TokensWithGrant(lease.Context).FirstOrDefaultAsync(t => t.Id == tokenId, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryRotateRefreshTokenAsync(
        Guid tokenId, Guid successorId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var claimed = await lease.Context.OAuthRefreshTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedById, successorId), ct);
        return claimed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryRedeemRefreshTokenGraceAsync(Guid tokenId, int maxRedemptions, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var claimed = await lease.Context.OAuthRefreshTokens
            .Where(t => t.Id == tokenId && t.GraceRedemptions < maxRedemptions)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.GraceRedemptions, t => t.GraceRedemptions + 1), ct);
        return claimed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> IsRefreshTokenSignedOutAsync(Guid tokenId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthRefreshTokens.AnyAsync(
            t => t.Id == tokenId && t.RevokedAt != null && t.ReplacedById == null, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RevokeRefreshTokenAsync(Guid tokenId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var revoked = await lease.Context.OAuthRefreshTokens
            .Where(t => t.Id == tokenId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        return revoked == 1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Runs only over a unit of work, where the owner has staged the grants' revocation on the same
    /// context: the two save together, so no token is revoked under a grant that stays live, and the
    /// grants' <c>RevokedAt</c>, unsaved at this point, cannot be in the predicate. The token's own
    /// <c>RevokedAt</c> is, so a rotation that won meanwhile keeps its stamp. Tracked rows rather
    /// than a bulk update: the callers' tests run on the EF InMemory provider, which has no
    /// <c>ExecuteUpdate</c>.
    /// </remarks>
    public virtual async Task<int> RevokeRefreshTokensOfGrantsAsync(
        IReadOnlyCollection<Guid> grantIds, DateTime now, CancellationToken ct)
    {
        var db = (_unit ?? throw new InvalidOperationException(
            "The tokens of a grant are revoked on the unit of work that revokes the grant.")).Context;
        if (grantIds.Count == 0)
            return 0;

        var ids = grantIds.ToList();
        var tokens = await db.OAuthRefreshTokens
            .Where(t => ids.Contains(t.GrantId) && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in tokens)
        {
            token.RevokedAt = now;
        }

        return tokens.Count;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> UserCodeExistsAsync(string userCode, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthDeviceCodes.AnyAsync(d => d.UserCode == userCode, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> StoreDeviceCodeAsync(NewDeviceCode code, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = new OAuthDeviceCodeEntity
        {
            ClientId = code.ClientId,
            DeviceCodeHash = code.DeviceCodeHash,
            UserCode = code.UserCode,
            Scopes = code.Scopes.ToList(),
            ExpiresAt = code.ExpiresAt,
            Interval = code.Interval,
        };
        db.OAuthDeviceCodes.Add(entity);
        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<DeviceCodeRecord?> FindDeviceCodeByUserCodeAsync(string userCode, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await lease.Context.OAuthDeviceCodes.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserCode == userCode, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<DeviceCodeRecord?> FindDeviceCodeAsync(string deviceCodeHash, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await lease.Context.OAuthDeviceCodes.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DeviceCodeHash == deviceCodeHash, ct));
    }

    /// <inheritdoc/>
    public virtual Task<bool> ApproveDeviceCodeAsync(
        Guid id, Guid subjectId, Guid grantId, DateTime now, CancellationToken ct) =>
        UpdateDeviceCodeAsync(id, d =>
        {
            d.ApprovedAt = now;
            d.GrantId = grantId;
            d.SubjectId = subjectId;
        }, ct);

    /// <inheritdoc/>
    public virtual Task<bool> DenyDeviceCodeAsync(Guid id, DateTime now, CancellationToken ct) =>
        UpdateDeviceCodeAsync(id, d => d.DeniedAt = now, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// A tracked save rather than an <c>ExecuteUpdate</c>: it is the save between a poll's read of
    /// the code and its claim, which is where a rival poll can redeem the code, and the exchange
    /// test for that race stages the rival in a <c>SaveChangesInterceptor</c>.
    /// </remarks>
    public virtual Task<bool> RecordDevicePollAsync(Guid id, DateTime polledAt, CancellationToken ct) =>
        UpdateDeviceCodeAsync(id, d => d.LastPolledAt = polledAt, ct);

    /// <inheritdoc/>
    public virtual async Task<bool> BumpDeviceCodeIntervalAsync(Guid id, int by, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var bumped = await lease.Context.OAuthDeviceCodes
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Interval, d => d.Interval + by), ct);
        return bumped == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryRedeemDeviceCodeAsync(Guid id, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var claimed = await lease.Context.OAuthDeviceCodes
            .Where(d => d.Id == id && d.RedeemedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RedeemedAt, now), ct);
        return claimed == 1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Pinned because the sweep runs from a hosted service, whose root-resolved factory reads as no
    /// tenant and under FORCE ROW LEVEL SECURITY would see none of the rows.
    /// </remarks>
    public virtual async Task<ExpiredCodeDeletion> DeleteExpiredCodesAsync(
        Guid tenantId, DateTime cutoff, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        var deviceCodes = await db.OAuthDeviceCodes
            .Where(d => d.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);
        var authorizationCodes = await db.OAuthAuthorizationCodes
            .Where(c => c.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);
        return new ExpiredCodeDeletion(deviceCodes, authorizationCodes);
    }

    private async Task<bool> UpdateDeviceCodeAsync(Guid id, Action<OAuthDeviceCodeEntity> change, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.OAuthDeviceCodes.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (entity is null)
            return false;

        change(entity);
        await SaveAsync(db, ct);
        return true;
    }

    private static IQueryable<OAuthRefreshTokenEntity> TokensWithGrant(NocturneDbContext db) =>
        db.OAuthRefreshTokens
            .AsNoTracking()
            .Include(t => t.Grant)
                .ThenInclude(g => g!.Client);

    private static OAuthRefreshTokenRecord? ToRecord(OAuthRefreshTokenEntity? token) =>
        token is null ? null : new OAuthRefreshTokenRecord(
            token.Id,
            token.TenantId,
            token.GrantId,
            token.ExpiresAt,
            token.RevokedAt,
            token.ReplacedById,
            token.GraceRedemptions,
            token.Grant is null ? null : new OAuthRefreshTokenGrant(
                token.Grant.Id,
                token.Grant.TenantId,
                token.Grant.SubjectId,
                token.Grant.Scopes,
                token.Grant.Client?.ClientId,
                token.Grant.LimitTo24Hours,
                token.Grant.IsRevoked));

    private static DeviceCodeRecord? ToRecord(OAuthDeviceCodeEntity? code) =>
        code is null ? null : new DeviceCodeRecord(
            code.Id,
            code.ClientId,
            code.UserCode,
            code.Scopes,
            code.ExpiresAt,
            code.ApprovedAt,
            code.DeniedAt,
            code.RedeemedAt,
            code.GrantId,
            code.SubjectId,
            code.Interval,
            code.LastPolledAt);
}
