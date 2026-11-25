using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ITenantCreationInviteRepository"/> over platform contexts, one per call. The use
/// claim is an <c>ExecuteUpdate</c> whose predicate is the cap, so the row's lock, not the change
/// tracker, decides which of two redemptions of a single-use link gets it.
/// </summary>
public class TenantCreationInviteRepository(INocturneDbContextFactory contexts) : ITenantCreationInviteRepository
{
    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewTenantCreationInvite invite, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var entity = new TenantCreationInviteEntity
        {
            Id = Guid.CreateVersion7(),
            CreatedBySubjectId = invite.CreatedBySubjectId,
            TokenHash = invite.TokenHash,
            Label = invite.Label,
            ExpiresAt = invite.ExpiresAt,
            MaxUses = invite.MaxUses,
        };
        db.TenantCreationInvites.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<TenantCreationInviteRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var entity = await db.TenantCreationInvites
            .AsNoTracking()
            .Include(i => i.CreatedBy)
            .FirstOrDefaultAsync(i => i.TokenHash == tokenHash, ct);
        return entity is null ? null : ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantCreationInviteRecord>> ListAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var entities = await db.TenantCreationInvites
            .AsNoTracking()
            .Include(i => i.CreatedBy)
            .Include(i => i.Uses)
                .ThenInclude(u => u.Tenant)
            .OrderByDescending(i => i.SysCreatedAt)
            .ToListAsync(ct);
        return entities.Select(ToRecord).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryClaimUseAsync(Guid inviteId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var claimed = await db.TenantCreationInvites
            .Where(i => i.Id == inviteId
                && i.RevokedAt == null
                && i.ExpiresAt > now
                && (i.MaxUses == null || i.UseCount < i.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UseCount, i => i.UseCount + 1), ct);
        return claimed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task ReleaseUseAsync(Guid inviteId, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        await db.TenantCreationInvites
            .Where(i => i.Id == inviteId && i.UseCount > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UseCount, i => i.UseCount - 1), ct);
    }

    /// <inheritdoc/>
    public virtual async Task RecordUseAsync(Guid inviteId, Guid tenantId, DateTime redeemedAt, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        db.TenantCreationInviteUses.Add(new TenantCreationInviteUseEntity
        {
            Id = Guid.CreateVersion7(),
            InviteId = inviteId,
            TenantId = tenantId,
            RedeemedAt = redeemedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RevokeAsync(Guid inviteId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var entity = await db.TenantCreationInvites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (entity is null)
            return false;

        if (entity.RevokedAt is null)
        {
            entity.RevokedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    private static TenantCreationInviteRecord ToRecord(TenantCreationInviteEntity invite) =>
        new(
            invite.Id,
            invite.CreatedBySubjectId,
            invite.CreatedBy?.Name,
            invite.Label,
            invite.ExpiresAt,
            invite.MaxUses,
            invite.UseCount,
            invite.RevokedAt,
            invite.SysCreatedAt,
            invite.Uses
                .OrderBy(u => u.RedeemedAt)
                .Select(u => new TenantCreationInviteUse(
                    u.TenantId, u.Tenant?.Slug, u.Tenant?.DisplayName, u.RedeemedAt))
                .ToList());
}

/// <summary><see cref="IPlatformPolicyRepository"/> over platform contexts, one per call.</summary>
public class PlatformPolicyRepository(INocturneDbContextFactory contexts, TimeProvider time) : IPlatformPolicyRepository
{
    /// <inheritdoc/>
    public virtual async Task<PlatformPolicy> GetAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await ReadAsync(db, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The row is inserted with the defaults when it is missing, then one <c>UPDATE</c> sets each
    /// switch to the value sent or to its own value, so the row lock orders two updates and neither
    /// writes back a switch it did not name.
    /// </remarks>
    public virtual async Task<PlatformPolicy> UpdateAsync(PlatformPolicyUpdate update, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO platform_policy (id, allow_owner_slug_change, restrict_team_creation_to_admins) VALUES ({PlatformPolicyEntity.SingletonId}, false, false) ON CONFLICT (id) DO NOTHING",
                token);
            await db.PlatformPolicy
                .Where(p => p.Id == PlatformPolicyEntity.SingletonId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.AllowOwnerSlugChange, p => update.AllowOwnerSlugChange ?? p.AllowOwnerSlugChange)
                    .SetProperty(p => p.RestrictTeamCreationToAdmins, p => update.RestrictTeamCreationToAdmins ?? p.RestrictTeamCreationToAdmins)
                    .SetProperty(p => p.SysUpdatedAt, now), token);
            return await ReadAsync(db, token);
        }, ct: ct);
    }

    private static async Task<PlatformPolicy> ReadAsync(NocturneDbContext db, CancellationToken ct)
    {
        var row = await db.PlatformPolicy.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == PlatformPolicyEntity.SingletonId, ct);
        return row is null
            ? new PlatformPolicy()
            : new PlatformPolicy(row.AllowOwnerSlugChange, row.RestrictTeamCreationToAdmins);
    }
}
