using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IMemberInviteRepository"/> over <see cref="NocturneDbContext"/>, one context per call,
/// or the caller's <see cref="DbUnitOfWork"/> for the use claim when constructed over one.
/// The use claim is an <c>ExecuteUpdate</c> whose predicate is the cap, so the row's lock, not the
/// change tracker, decides which accept of a single-use invite joins.
/// </summary>
public class MemberInviteRepository : IMemberInviteRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context.</summary>
    public MemberInviteRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose use claim runs on <paramref name="unit"/>, which the caller owns and has
    /// pinned. The claim is a statement, not a staged write, so it joins the unit's transaction only
    /// when called inside the work of <see cref="DbUnitOfWork.CommitAsync{T}"/>, where the owner's
    /// rollback releases it.
    /// </summary>
    public MemberInviteRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private INocturneDbContextFactory Standalone => _contexts ?? throw new InvalidOperationException(
        "A repository over a unit of work serves only the use claim.");

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewMemberInvite invite, CancellationToken ct)
    {
        await using var db = await Standalone.CreateAsync(ct);
        var entity = new MemberInviteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = invite.TenantId,
            CreatedBySubjectId = invite.CreatedBySubjectId,
            TokenHash = invite.TokenHash,
            RoleIds = invite.RoleIds.ToList(),
            DirectPermissions = invite.DirectPermissions?.ToList(),
            Label = invite.Label,
            LimitTo24Hours = invite.LimitTo24Hours,
            ExpiresAt = invite.ExpiresAt,
            MaxUses = invite.MaxUses,
            UseCount = 0,
            CreatedAt = invite.CreatedAt,
        };
        db.MemberInvites.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<MemberInviteRecord?> FindByTokenHashAsync(string tokenHash, Guid tenantId, CancellationToken ct)
    {
        await using var db = await Standalone.CreateAsync(ct);
        var entity = await db.MemberInvites
            .AsNoTracking()
            .Include(i => i.Tenant)
            .Include(i => i.CreatedBy)
            .Where(i => i.TokenHash == tokenHash && i.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : ToRecord(entity, withUses: false);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TryClaimUseAsync(Guid inviteId, CancellationToken ct)
    {
        await using var lease = await DbContextLease.OpenAsync(_unit, _contexts, ct);
        var claimed = await lease.Context.MemberInvites
            .Where(i => i.Id == inviteId && (i.MaxUses == null || i.UseCount < i.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UseCount, i => i.UseCount + 1), ct);
        return claimed == 1;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<MemberInviteRecord>> GetForTenantAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Standalone.CreateAsync(ct);
        var entities = await db.MemberInvites
            .AsNoTracking()
            .Include(i => i.Tenant)
            .Include(i => i.CreatedBy)
            .Include(i => i.CreatedMembers)
                .ThenInclude(m => m.Subject)
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(e => ToRecord(e, withUses: true)).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<MemberInviteRevocation> RevokeAsync(
        Guid inviteId, Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var db = await Standalone.CreateAsync(ct);
        var entity = await db.MemberInvites
            .Where(i => i.Id == inviteId && i.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        if (entity is null)
            return MemberInviteRevocation.NotFound;
        if (entity.RevokedAt.HasValue)
            return MemberInviteRevocation.AlreadyRevoked;

        entity.RevokedAt = now;
        await db.SaveChangesAsync(ct);
        return MemberInviteRevocation.Revoked;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyUsedOrLiveAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await Standalone.CreateAsync(ct);
        return await db.MemberInvites.AnyAsync(
            i => i.UseCount > 0
                 || (i.RevokedAt == null && i.ExpiresAt > now && (i.MaxUses == null || i.UseCount < i.MaxUses)),
            ct);
    }

    private static MemberInviteRecord ToRecord(MemberInviteEntity invite, bool withUses) =>
        new(
            invite.Id,
            invite.TenantId,
            invite.Tenant?.DisplayName,
            invite.CreatedBySubjectId,
            invite.CreatedBy?.Name,
            invite.RoleIds,
            invite.DirectPermissions,
            invite.Label,
            invite.LimitTo24Hours,
            invite.ExpiresAt,
            invite.MaxUses,
            invite.UseCount,
            invite.RevokedAt,
            invite.CreatedAt,
            withUses
                ? invite.CreatedMembers
                    .Select(m => new MemberInviteUse(m.SubjectId, m.Subject?.Name, m.SysCreatedAt))
                    .ToList()
                : []);
}
