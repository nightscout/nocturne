using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IMemberInviteRepository"/> over <see cref="NocturneDbContext"/>, one context per call,
/// or the caller's <see cref="DbUnitOfWork"/> for the use claim and the revocation of a removed maker's
/// invites when constructed over one.
/// The use claim is an <c>ExecuteUpdate</c> whose predicate is the cap, the invite being unrevoked and its
/// maker being a member in force, so the row's lock, not the change tracker, decides which accept of a
/// single-use invite joins, and an accept that meets the revocation a removal staged on the same row is refused.
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
    /// A repository whose use claim and revocation of a maker's invites run on <paramref name="unit"/>,
    /// which the caller owns and has pinned. The claim is a statement, not a staged write, so it joins
    /// the unit's transaction only when called inside the work of <see cref="DbUnitOfWork.CommitAsync{T}"/>,
    /// where the owner's rollback releases it.
    /// </summary>
    public MemberInviteRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private INocturneDbContextFactory Standalone => _contexts ?? throw new InvalidOperationException(
        "A repository over a unit of work serves only the use claim and the revocation of a maker's invites.");

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
            AccessEndsAt = invite.AccessEndsAt,
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
        await using var db = await Standalone.CreateForTenantAsync(tenantId, ct);
        var entity = await db.MemberInvites
            .AsNoTracking()
            .Include(i => i.Tenant)
            .Include(i => i.CreatedBy)
            .Where(i => i.TokenHash == tokenHash && i.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
        return entity is null ? null : ToRecord(entity, withUses: false);
    }

    /// <inheritdoc/>
    public virtual async Task<MemberInviteClaim> TryClaimUseAsync(Guid inviteId, DateTime now, CancellationToken ct)
    {
        await using var lease = await DbContextLease.OpenAsync(_unit, _contexts, ct);
        var db = lease.Context;
        var claimed = await db.MemberInvites
            .Where(i => i.Id == inviteId && i.RevokedAt == null && (i.MaxUses == null || i.UseCount < i.MaxUses))
            .Where(MadeByMemberInForce(db, now))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UseCount, i => i.UseCount + 1), ct);
        if (claimed == 1)
            return MemberInviteClaim.Claimed;

        return await db.MemberInvites.AnyAsync(i => i.Id == inviteId && i.MaxUses != null && i.UseCount >= i.MaxUses, ct)
            ? MemberInviteClaim.Exhausted
            : MemberInviteClaim.Revoked;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> StageRevokeLiveMadeByAsync(
        Guid tenantId, Guid makerSubjectId, DateTime now, CancellationToken ct)
    {
        var unit = _unit ?? throw new InvalidOperationException(
            "Revoking the invites a member made stages audit rows with the change, so it needs the caller's unit of work.");
        var made = await unit.Context.MemberInvites
            .Where(i => i.TenantId == tenantId && i.CreatedBySubjectId == makerSubjectId)
            .Where(CanBeAcceptedAt(now))
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
        foreach (var invite in made)
        {
            invite.RevokedAt = now;
            MemberAccessAudit.StageMemberInviteRevoked(unit.Context, tenantId, makerSubjectId, invite.Id);
        }

        return made.Select(i => i.Id).ToList();
    }

    /// <inheritdoc/>
    public virtual void UnstageRevokeLiveMadeBy(IReadOnlyCollection<Guid> inviteIds)
    {
        var unit = _unit ?? throw new InvalidOperationException(
            "Unstaging a revocation acts on the caller's unit of work.");
        foreach (var entry in unit.Context.ChangeTracker.Entries<MemberInviteEntity>()
                     .Where(e => inviteIds.Contains(e.Entity.Id)).ToList())
            entry.State = EntityState.Detached;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlySet<Guid>> GetMakersInForceAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var db = await Standalone.CreateAsync(ct);
        return (await db.MemberInvites
                .Where(i => i.TenantId == tenantId)
                .Where(MadeByMemberInForce(db, now))
                .Select(i => i.CreatedBySubjectId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();
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
        return await db.MemberInvites.AnyAsync(i => i.UseCount > 0, ct)
               || await db.MemberInvites.Where(CanBeAcceptedAt(now)).Where(MadeByMemberInForce(db, now)).AnyAsync(ct);
    }

    private static IQueryable<TenantMemberEntity> MembersInForce(NocturneDbContext db, DateTime now) =>
        db.TenantMembers.Where(MembershipLiveness.IsLive(now));

    private static Expression<Func<MemberInviteEntity, bool>> MadeByMemberInForce(NocturneDbContext db, DateTime now) =>
        i => MembersInForce(db, now).Any(m => m.TenantId == i.TenantId && m.SubjectId == i.CreatedBySubjectId);

    private static Expression<Func<MemberInviteEntity, bool>> CanBeAcceptedAt(DateTime now) =>
        i => i.RevokedAt == null && i.ExpiresAt > now && (i.MaxUses == null || i.UseCount < i.MaxUses);

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
                : [],
            invite.AccessEndsAt);
}
