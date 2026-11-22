using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ITenantMembershipRepository"/> over <see cref="NocturneDbContext"/>: one context
/// per call, pinned as the contract says, saving each write, or the caller's
/// <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds: the authentication middleware, the member
/// scope middleware and the hub revalidation resolve it from whatever provider they hold, and a
/// root-resolved repository reads as no one. Writes are tracked reads and saves rather than bulk
/// statements; the one exception, the last-used stamp a request leaves behind, says why.
/// </remarks>
public class TenantMembershipRepository : ITenantMembershipRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;
    private readonly IAccountLinkRoute _linkRoute;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    /// <param name="contexts">Opens the context of each call.</param>
    /// <param name="linkRoute">Decides who cannot sign in (<see cref="OrphanedSubjectFilter"/>).</param>
    public TenantMembershipRepository(INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _linkRoute = linkRoute ?? throw new ArgumentNullException(nameof(linkRoute));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns and has
    /// pinned. A write is staged on the unit and not saved: the owner saves it, so a membership
    /// lands in one transaction with the tenant, roles or grants beside it.
    /// </summary>
    /// <param name="unit">The caller's unit of work.</param>
    /// <param name="linkRoute">Decides who cannot sign in (<see cref="OrphanedSubjectFilter"/>).</param>
    public TenantMembershipRepository(DbUnitOfWork unit, IAccountLinkRoute linkRoute)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _linkRoute = linkRoute ?? throw new ArgumentNullException(nameof(linkRoute));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private ValueTask<DbContextLease> OpenForTenantAsync(Guid tenantId, CancellationToken ct) =>
        DbContextLease.OpenForTenantAsync(_unit, _contexts, tenantId, ct);

    private ValueTask<DbContextLease> OpenPlatformAsync(CancellationToken ct) =>
        DbContextLease.OpenPlatformAsync(_unit, _contexts, ct);

    private INocturneDbContextFactory Pinnable => _contexts ?? throw new InvalidOperationException(
        "A repository over a unit of work runs on the unit's context and cannot pin another.");

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private IQueryable<TenantMemberEntity> Members(NocturneDbContext db) =>
        db.TenantMembers.AsNoTracking();

    private IQueryable<TenantMemberEntity> MembersWithDetails(NocturneDbContext db) =>
        Members(db)
            .Include(m => m.Subject)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole);

    private IQueryable<TenantRoleEntity> Roles(NocturneDbContext db) =>
        db.TenantRoles.AsNoTracking();

    /// <inheritdoc/>
    public virtual async Task<bool> AddMemberAsync(NewTenantMember member, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(member.TenantId, ct);
        var db = lease.Context;
        // A row exists exactly when a direct reason does only while direct is the only kind of reason.
        if (await db.TenantMembers.AnyAsync(m => m.TenantId == member.TenantId && m.SubjectId == member.SubjectId, ct))
            return false;

        await StageAddAsync(db, member, ct);

        if (_unit is not null)
            return true;

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another request made the subject a member between the check and the save.
            return false;
        }
    }

    /// <summary>
    /// Stages <paramref name="member"/> on <paramref name="db"/> through <see cref="MembershipReasons"/>,
    /// with the audit rows of what it is given, attributed as its <see cref="NewTenantMember.MadeBy"/> says.
    /// </summary>
    internal static async Task<TenantMemberEntity> StageAddAsync(NocturneDbContext db, NewTenantMember member, CancellationToken ct)
    {
        var entity = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = member.TenantId,
            SubjectId = member.SubjectId,
            Label = member.Label,
            Username = member.Username,
        };
        var roleIds = member.RoleIds.Distinct().ToList();
        var source = member.MadeBy switch
        {
            MembershipMadeBy.Member => new MemberAccessSource(member.SubjectId, member.CreatedFromInviteId),
            MembershipMadeBy.System => MemberAccessSource.System,
            _ => null,
        };
        MembershipReasons.AddMember(
            db, entity,
            new MembershipGrant(roleIds, member.DirectPermissions, member.LimitTo24Hours, member.AccessEndsAt),
            member.CreatedFromInviteId, member.How, member.By, source);

        MemberAccessAudit.StageRoles(db, member.TenantId, member.SubjectId, await SlugsOfAsync(db, roleIds, ct), [], source);
        MemberAccessAudit.StagePermissions(db, member.TenantId, member.SubjectId, null, entity.DirectPermissions, source);
        MemberAccessAudit.StageHistoryLimit(db, member.TenantId, member.SubjectId, false, entity.LimitTo24Hours, source);
        MemberAccessAudit.StageAccessEnd(db, member.TenantId, member.SubjectId, null, entity.AccessEndsAt, source);
        return entity;
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberRecord?> FindMemberAsync(Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMember(await MembersWithDetails(lease.Context)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberRecord?> GetMemberAsync(Guid tenantId, Guid memberId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMember(await MembersWithDetails(lease.Context)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberRecord?> FindMemberByUsernameAsync(Guid tenantId, string username, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMember(await MembersWithDetails(lease.Context)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Username == username, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<bool> UsernameExistsAsync(Guid tenantId, string username, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.Username == username, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberRecord?> FindPublicMemberAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMember(await MembersWithDetails(lease.Context)
            .Where(m => m.TenantId == tenantId)
            .OfPublicSubject()
            .FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberAccess?> GetMemberAccessAsync(Guid memberId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var member = await Members(lease.Context)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
            .FirstOrDefaultAsync(m => m.Id == memberId, ct);
        return ToAccess(member);
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberAccess?> GetMemberAccessAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var member = await Members(lease.Context)
            .LiveAt(now)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
        return ToAccess(member);
    }

    private static TenantMemberAccess? ToAccess(TenantMemberEntity? member) =>
        member is null
            ? null
            : new TenantMemberAccess(member.EffectivePermissions().ToHashSet(), member.LimitTo24Hours, member.AccessEndsAt);

    /// <inheritdoc/>
    public virtual async Task<bool> IsMemberAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .AnyAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> GetMemberRoleNamesAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
            .SelectMany(m => m.MemberRoles)
            .Select(mr => mr.TenantRole.Name)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantMemberRecord>> ListMembersAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMembers(await MembersWithDetails(lease.Context).Where(m => m.TenantId == tenantId).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantMemberRecord>> ListPeopleAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMembers(await MembersWithDetails(lease.Context).PeopleOf(tenantId).InJoinOrder().ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantMemberRecord>> ListLivePeopleAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMembers(await MembersWithDetails(lease.Context).LiveAt(now).PeopleOf(tenantId).InJoinOrder().ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantMemberRecord>> ListOwnersAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return ToMembers(await MembersWithDetails(lease.Context).OwnersOf(tenantId).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> GetOwnerSubjectIdsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking().OwnersOf(tenantId).Select(m => m.SubjectId).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<string?> GetEarliestOwnerPreferencesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking()
            .OwnersOf(tenantId)
            .Select(m => m.Subject!.Preferences)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyOtherOwnerAsync(Guid tenantId, Guid memberId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking().OwnersOf(tenantId).AnyAsync(m => m.Id != memberId, ct);
    }

    /// <summary>
    /// Runs <paramref name="change"/> under the tenant's owner lock (<see cref="TenantOwnerLock"/>)
    /// and saves it when it reports <see cref="OwnerGuardedChange.Changed"/>: on this repository's unit,
    /// whose owner commits it, else on a new unit pinned to <paramref name="tenantId"/>.
    /// </summary>
    /// <remarks>
    /// Holding the lock from the check to the commit makes the second of two owners stepping down or
    /// removing each other read the first one's write.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The repository is over a unit with no transaction open.</exception>
    private async Task<OwnerGuardedChange> UnderOwnersLockAsync(
        Guid tenantId,
        Func<NocturneDbContext, CancellationToken, Task<OwnerGuardedChange>> change,
        CancellationToken ct)
    {
        if (_unit is not null)
        {
            await _unit.Context.LockTenantOwnersAsync(tenantId, ct);
            return await change(_unit.Context, ct);
        }

        await using var unit = await DbUnitOfWork.OpenForTenantAsync(Pinnable, tenantId, ct);
        return await unit.CommitAsync(
            async token =>
            {
                await unit.Context.LockTenantOwnersAsync(tenantId, token);
                var outcome = await change(unit.Context, token);
                if (outcome == OwnerGuardedChange.Changed)
                    await unit.Context.SaveChangesAsync(token);
                return outcome;
            },
            ct: ct);
    }

    /// <inheritdoc/>
    public virtual Task<OwnerGuardedChange> RemoveMemberUnlessLastOwnerAsync(
        Guid tenantId, Guid memberId, MembershipEndHow how, CancellationToken ct) =>
        UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            if (await db.IsLastOwnerAsync(tenantId, memberId, _linkRoute, token))
                return OwnerGuardedChange.LastOwner;

            var member = await db.TenantMembers
                .Include(m => m.MemberRoles)
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null)
                return OwnerGuardedChange.NotFound;

            await StageRemoveAsync(db, member, how, token);
            return OwnerGuardedChange.Changed;
        }, ct);

    /// <inheritdoc/>
    public virtual async Task<SubjectMembershipsRemoval> RemoveMembershipsOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        if (_unit is not null)
            return await RemoveMembershipsUnderOwnerLocksAsync(_unit.Context, subjectId, _linkRoute, ct);

        await using var unit = await DbUnitOfWork.OpenPlatformAsync(Pinnable, ct);
        return await unit.CommitAsync(
            async token =>
            {
                var removal = await RemoveMembershipsUnderOwnerLocksAsync(unit.Context, subjectId, _linkRoute, token);
                await unit.Context.SaveChangesAsync(token);
                return removal;
            },
            ct: ct);
    }

    private static async Task<SubjectMembershipsRemoval> RemoveMembershipsUnderOwnerLocksAsync(
        NocturneDbContext db, Guid subjectId, IAccountLinkRoute linkRoute, CancellationToken ct)
    {
        var leftWithoutOwner = await db.LockTenantsOfSubjectAsync(subjectId, linkRoute, ct);
        var memberships = await db.TenantMembers
            .Include(m => m.MemberRoles)
            .Where(m => m.SubjectId == subjectId)
            .ToListAsync(ct);

        foreach (var membership in memberships)
            await StageRemovalAsync(db, membership, ct);
        foreach (var membership in memberships)
            await MembershipReasons.RemoveMemberAsync(db, membership, MembershipEndHow.System, ct);
        return new SubjectMembershipsRemoval(memberships.Count, leftWithoutOwner);
    }

    /// <summary>
    /// Stages the removal of <paramref name="member"/>, whose role links are loaded, with its audit rows,
    /// the removed row last.
    /// </summary>
    internal static async Task StageRemoveAsync(
        NocturneDbContext db, TenantMemberEntity member, MembershipEndHow how, CancellationToken ct)
    {
        await StageRemovalAsync(db, member, ct);
        await MembershipReasons.RemoveMemberAsync(db, member, how, ct);
    }

    /// <summary>
    /// Stages the audit of a membership being removed: each role and direct permission it held is
    /// removed. Its history limit is not, since the member keeps no access for it to apply to.
    /// </summary>
    private static async Task StageRemovalAsync(NocturneDbContext db, TenantMemberEntity member, CancellationToken ct)
    {
        MemberAccessAudit.StageRoles(
            db, member.TenantId, member.SubjectId, [],
            await SlugsOfAsync(db, member.MemberRoles.Select(mr => mr.TenantRoleId).ToList(), ct));
        MemberAccessAudit.StagePermissions(db, member.TenantId, member.SubjectId, member.DirectPermissions, null);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetDirectPermissionsAsync(
        Guid tenantId, Guid memberId, IReadOnlyList<string>? permissions, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var member = await lease.Context.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, ct);
        if (member is null)
            return false;

        MemberAccessAudit.StagePermissions(lease.Context, tenantId, member.SubjectId, member.DirectPermissions, permissions);
        await MembershipReasons.SetDirectPermissionsAsync(lease.Context, member, permissions, ct);
        member.SysUpdatedAt = now;
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetLimitTo24HoursAsync(Guid tenantId, Guid memberId, bool limitTo24Hours, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var member = await lease.Context.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, ct);
        if (member is null)
            return false;

        MemberAccessAudit.StageHistoryLimit(lease.Context, tenantId, member.SubjectId, member.LimitTo24Hours, limitTo24Hours);
        await MembershipReasons.SetLimitTo24HoursAsync(lease.Context, member, limitTo24Hours, ct);
        member.SysUpdatedAt = now;
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual Task<OwnerGuardedChange> SetAccessEndUnlessOwnerAsync(
        Guid tenantId, Guid memberId, DateTime? accessEndsAt, DateTime now, CancellationToken ct) =>
        UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            var member = await db.TenantMembers
                .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null)
                return OwnerGuardedChange.NotFound;

            if (accessEndsAt is not null && member.MemberRoles.Any(mr => mr.TenantRole.Slug == RoleSeeds.Owner))
                return OwnerGuardedChange.OwnerAccessEnds;

            MemberAccessAudit.StageAccessEnd(db, tenantId, member.SubjectId, member.AccessEndsAt, accessEndsAt);
            if (member.AccessEndsAt != accessEndsAt)
            {
                await MembershipReasons.SetAccessEndAsync(db, member, accessEndsAt, token);
                member.SysUpdatedAt = now;
            }

            if (accessEndsAt is { } end)
                await AlertChannelStops.CapAsync(db, member.SubjectId, end, token);

            return OwnerGuardedChange.Changed;
        }, ct);

    /// <inheritdoc/>
    public virtual async Task<bool> SetUsernameAsync(Guid tenantId, Guid memberId, string? username, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var member = await lease.Context.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, ct);
        if (member is null)
            return false;

        member.Username = username;
        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> ReplaceRolesAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        if (!await StageRoleReplacementAsync(lease.Context, tenantId, memberId, removedRoleIds, addedRoleIds, now, ct))
            return false;

        await SaveAsync(lease.Context, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual Task<OwnerGuardedChange> ReplaceRolesUnlessLastOwnerAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct) =>
        UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            if (await Roles(db).AnyAsync(r => r.TenantId == tenantId && r.Slug == RoleSeeds.Owner && removedRoleIds.Contains(r.Id), token)
                && await db.IsLastOwnerAsync(tenantId, memberId, _linkRoute, token))
                return OwnerGuardedChange.LastOwner;

            if (await Roles(db).AnyAsync(r => r.TenantId == tenantId && r.Slug == RoleSeeds.Owner && addedRoleIds.Contains(r.Id), token)
                && await db.TenantMembers.AnyAsync(m => m.TenantId == tenantId && m.Id == memberId && m.AccessEndsAt != null, token))
                return OwnerGuardedChange.OwnerAccessEnds;

            return await StageRoleReplacementAsync(db, tenantId, memberId, removedRoleIds, addedRoleIds, now, token)
                ? OwnerGuardedChange.Changed
                : OwnerGuardedChange.NotFound;
        }, ct);

    private static async Task<bool> StageRoleReplacementAsync(
        NocturneDbContext db, Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds,
        IReadOnlyCollection<Guid> addedRoleIds, DateTime now, CancellationToken ct)
    {
        var member = await db.TenantMembers
            .Include(m => m.MemberRoles)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, ct);
        if (member is null)
            return false;

        var before = member.MemberRoles.Select(mr => mr.TenantRoleId).ToHashSet();
        var after = before.Where(id => !removedRoleIds.Contains(id)).Concat(addedRoleIds).ToHashSet();
        MemberAccessAudit.StageRoles(
            db, tenantId, member.SubjectId,
            await SlugsOfAsync(db, after.Except(before).ToList(), ct),
            await SlugsOfAsync(db, before.Except(after).ToList(), ct));

        await MembershipReasons.ReplaceRolesAsync(db, member, removedRoleIds, addedRoleIds, ct);
        member.SysUpdatedAt = now;
        return true;
    }

    private static async Task<List<string>> SlugsOfAsync(NocturneDbContext db, IReadOnlyCollection<Guid> roleIds, CancellationToken ct) =>
        roleIds.Count == 0
            ? []
            : await db.TenantRoles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => r.Slug).ToListAsync(ct);

    /// <inheritdoc/>
    public virtual async Task RecordUseAsync(
        Guid tenantId, Guid memberId, DateTime at, string? ipAddress, string? userAgent, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        // One statement: the stamp outlives the request that earned it, so nothing is tracked.
        var staleBefore = LastUsedStamp.StaleBefore(at);
        await lease.Context.TenantMembers
            .Where(m => m.Id == memberId && m.TenantId == tenantId
                && (m.LastUsedAt == null || m.LastUsedAt < staleBefore))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.LastUsedAt, at)
                .SetProperty(m => m.LastUsedIp, ipAddress)
                .SetProperty(m => m.LastUsedUserAgent, userAgent), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectMembershipRead?> ReadSubjectMembershipAsync(
        Guid subjectId, Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var db = lease.Context;
        var row = await db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .GroupJoin(
                db.TenantMembers.AsNoTracking().LiveAt(now).Where(m => m.TenantId == tenantId),
                s => s.Id,
                m => m.SubjectId,
                (s, members) => new { s, members })
            .SelectMany(
                joined => joined.members.DefaultIfEmpty(),
                (joined, m) => new MembershipRow
                {
                    IsPlatformAdmin = joined.s.IsPlatformAdmin && joined.s.IsActive,
                    IsActive = joined.s.IsActive,
                    MemberId = (Guid?)m!.Id,
                    DirectPermissions = m.DirectPermissions,
                    RolePermissions = m.MemberRoles.Select(mr => mr.TenantRole.Permissions).ToList(),
                    LimitTo24Hours = (bool?)m.LimitTo24Hours,
                    AccessEndsAt = m.AccessEndsAt,
                    LastUsedAt = m.LastUsedAt,
                })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : new SubjectMembershipRead(row.IsPlatformAdmin, row.IsActive, row.Facts());
    }

    /// <summary>The subject's flag with the membership columns of the left join; <see cref="MemberId"/> is null when there is none.</summary>
    private sealed class MembershipRow
    {
        public bool IsPlatformAdmin { get; init; }

        public bool IsActive { get; init; }

        public Guid? MemberId { get; init; }

        public List<string>? DirectPermissions { get; init; }

        public List<List<string>> RolePermissions { get; init; } = [];

        public bool? LimitTo24Hours { get; init; }

        public DateTime? AccessEndsAt { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public TenantMembershipFacts? Facts() => MemberId is { } id
            ? new TenantMembershipFacts(
                id,
                new TenantMemberAccess(
                    MemberPermissions.Effective(RolePermissions, DirectPermissions).ToHashSet(),
                    LimitTo24Hours == true,
                    AccessEndsAt),
                LastUsedAt)
            : null;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> ListTenantIdsOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        return await db.TenantMembers.AsNoTracking()
            .Where(m => m.SubjectId == subjectId)
            .Select(m => m.TenantId)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantRecord>> ListTenantsOfSubjectAsync(
        Guid subjectId, bool activeOnly, DateTime now, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        var rows = await db.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .Where(m => m.SubjectId == subjectId && (!activeOnly || m.Tenant!.IsActive))
            .OrderByDescending(m => m.MemberRoles.Any(mr => mr.TenantRole!.Slug == RoleSeeds.Owner))
            .ThenBy(m => m.SysCreatedAt)
            .ThenBy(m => m.Tenant!.Slug)
            .Select(m => new { Tenant = m.Tenant!, m.Tenant!.DemoConfig })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            r.Tenant.DemoConfig = r.DemoConfig;
            return TenantRepository.ToRecord(r.Tenant)!;
        }).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SubjectMembership>> ListMembershipsOfSubjectAsync(
        Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        var memberships = await db.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .Include(m => m.Tenant).ThenInclude(t => t!.DemoConfig)
            .Include(m => m.Subject)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
            .Where(m => m.SubjectId == subjectId)
            .ToListAsync(ct);
        return memberships
            .Where(m => m.Tenant is not null)
            .Select(m => new SubjectMembership(TenantRepository.ToRecord(m.Tenant)!, ToMember(m)!))
            .ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<SetupGate?> ReadSetupGateAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        // One statement on every API request: no member row means no member can hold a credential.
        // Credentials and OIDC identities are subject-scoped, so both checks join through the members.
        var members = db.TenantMembers.Where(m => m.TenantId == tenantId);
        var stuck = db.OrphanedSubjectsOf(tenantId, _linkRoute);
        var gate = await members
            .Take(1)
            .Select(_ => new
            {
                HasCredentials = members.Any(m =>
                    db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                    db.WorkingOidcIdentities().Any(i => i.SubjectId == m.SubjectId)),
                HasStuckMember = stuck.Any(),
            })
            .FirstOrDefaultAsync(ct);
        if (gate is null)
            return null;
        if (!gate.HasCredentials || !gate.HasStuckMember)
            return new SetupGate(gate.HasCredentials, RecoveryMode: false);

        return new SetupGate(HasCredentials: true, RecoveryMode: !await AnAdministratorCanSignInAsync(db, tenantId, stuck, now, ct));
    }

    /// <summary>
    /// Whether a person on the tenant who is not in <paramref name="stuck"/>, and whose membership
    /// has not ended at <paramref name="now"/>, holds <see cref="Scope.MembersManage"/>, read only for
    /// a site with a stuck member.
    /// </summary>
    /// <remarks>
    /// The permission evaluation stays in .NET (<see cref="TenantMemberEntity.Holds"/>), and is asked
    /// of each role and of each set of direct permissions apart: normalising expands each atom on its
    /// own and a requirement is met by a single atom, so a membership holds a scope exactly when one
    /// of its roles or its direct permissions does. The roles are a few rows; the members are asked
    /// for only through those that hold it, and only those with direct permissions are loaded.
    /// </remarks>
    private static async Task<bool> AnAdministratorCanSignInAsync(
        NocturneDbContext db, Guid tenantId, IQueryable<SubjectEntity> stuck, DateTime now, CancellationToken ct)
    {
        var reachable = db.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .PeopleOf(tenantId)
            .Where(m => !m.Subject!.IsDemoSubject && !stuck.Any(s => s.Id == m.SubjectId));

        var roles = await db.TenantRoles.AsNoTracking().Where(r => r.TenantId == tenantId).ToListAsync(ct);
        var administratorRoles = roles
            .Where(role => new TenantMemberEntity { MemberRoles = [new TenantMemberRoleEntity { TenantRole = role }] }
                .Holds(Scope.MembersManage))
            .Select(role => role.Id)
            .ToList();
        if (administratorRoles.Count > 0
            && await reachable.AnyAsync(m => m.MemberRoles.Any(mr => administratorRoles.Contains(mr.TenantRoleId)), ct))
            return true;

        var directGrants = await reachable
            .Where(m => m.DirectPermissions != null)
            .Select(m => m.DirectPermissions)
            .ToListAsync(ct);
        return directGrants.Any(direct => new TenantMemberEntity { DirectPermissions = direct }.Holds(Scope.MembersManage));
    }

    /// <inheritdoc/>
    public virtual async Task<SetupCredentialCounts> CountSetupCredentialsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        var members = db.TenantMembers.Where(m => m.TenantId == tenantId);
        var memberCount = await members.CountAsync(ct);
        if (memberCount == 0)
            return new SetupCredentialCounts(0, 0, 0);

        var passkeys = await members
            .SelectMany(m => db.PasskeyCredentials.Where(c => c.SubjectId == m.SubjectId))
            .CountAsync(ct);
        var identities = await members
            .SelectMany(m => db.WorkingOidcIdentities().Where(i => i.SubjectId == m.SubjectId))
            .CountAsync(ct);
        return new SetupCredentialCounts(memberCount, passkeys, identities);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OrphanedMember>> ListOrphanedMembersAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        return await db.OrphanedSubjectsOf(tenantId, _linkRoute)
            .Select(s => new OrphanedMember(s.Id, s.Name, s.Username))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<PlatformAdminDirectory> ListPlatformAdminsAsync(CancellationToken ct)
    {
        await using var lease = await OpenPlatformAsync(ct);
        var db = lease.Context;
        var memberships = await db.TenantMembers.AsNoTracking()
            .Where(m => m.Subject!.IsPlatformAdmin && m.Subject.IsActive && !m.Subject.IsSystemSubject
                && m.Tenant!.IsActive)
            .OrderBy(m => m.SysCreatedAt).ThenBy(m => m.Id)
            .Select(m => new PlatformAdminMembership(m.SubjectId, m.TenantId, m.Tenant!.Slug, m.Tenant.DisplayName))
            .ToListAsync(ct);
        var admins = memberships.GroupBy(m => m.SubjectId).Select(g => g.First()).ToList();
        var count = await db.Subjects.AsNoTracking()
            .CountAsync(s => s.IsPlatformAdmin && s.IsActive && !s.IsSystemSubject, ct);
        return new PlatformAdminDirectory(admins, count);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantRoleRecord>> ListRolesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var rows = await Roles(lease.Context)
            .Where(r => r.TenantId == tenantId)
            .Select(r => new { Role = r, MemberCount = r.MemberRoles.Count })
            .ToListAsync(ct);
        return rows.Select(row => ToRole(row.Role, row.MemberCount)).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRoleRecord?> GetRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var row = await Roles(lease.Context)
            .Where(r => r.Id == roleId && r.TenantId == tenantId)
            .Select(r => new { Role = r, MemberCount = r.MemberRoles.Count })
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToRole(row.Role, row.MemberCount);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid?> FindRoleIdBySlugAsync(Guid tenantId, string slug, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Slug == slug)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<IReadOnlyList<string>>> ListRolePermissionsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        if (roleIds.Count == 0)
            return [];

        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var ids = roleIds.ToList();
        var sets = await lease.Context.TenantRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && ids.Contains(r.Id))
            .Select(r => r.Permissions)
            .ToListAsync(ct);
        return sets.Select(IReadOnlyList<string> (s) => s).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRoleRecord> CreateRoleAsync(NewTenantRole role, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(role.TenantId, ct);
        var entity = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = role.TenantId,
            Name = role.Name,
            Slug = role.Slug,
            Description = role.Description,
            Permissions = [.. role.Permissions],
            IsSystem = role.IsSystem,
        };
        lease.Context.TenantRoles.Add(entity);
        await SaveAsync(lease.Context, ct);
        return ToRole(entity, 0);
    }

    /// <inheritdoc/>
    public virtual async Task<TenantRoleRecord?> UpdateRoleAsync(
        Guid tenantId, Guid roleId, TenantRoleEdit edit, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var entity = await lease.Context.TenantRoles
            .Include(r => r.MemberRoles)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, ct);
        if (entity is null)
            return null;

        MemberAccessAudit.StageRolePermissions(lease.Context, tenantId, roleId, edit.Slug, entity.Permissions, edit.Permissions);
        entity.Name = edit.Name;
        entity.Slug = edit.Slug;
        entity.Description = edit.Description;
        entity.Permissions = [.. edit.Permissions];
        entity.SysUpdatedAt = now;
        await SaveAsync(lease.Context, ct);
        return ToRole(entity, entity.MemberRoles.Count);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> DeleteRoleAsync(Guid tenantId, Guid roleId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var db = lease.Context;
        var role = await db.TenantRoles
            .Include(r => r.MemberRoles).ThenInclude(mr => mr.TenantMember)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, ct);
        if (role is null)
            return false;

        foreach (var holder in role.MemberRoles)
            MemberAccessAudit.StageRoles(db, tenantId, holder.TenantMember.SubjectId, [], [role.Slug]);
        await MembershipReasons.RemoveRoleAsync(db, role.Id, ct);
        db.TenantRoles.Remove(role);
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task SeedRolesAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var db = lease.Context;
        var existing = await db.TenantRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.Slug)
            .ToListAsync(ct);

        foreach (var (slug, permissions) in RoleSeeds.Permissions)
        {
            if (existing.Contains(slug))
                continue;

            db.TenantRoles.Add(new TenantRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = RoleSeeds.DisplayNames[slug],
                Slug = slug,
                Description = null,
                Permissions = [.. permissions],
                IsSystem = true,
            });
        }

        await SaveAsync(db, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateRequestAsync(NewMembershipRequest request, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(request.TenantId, ct);
        var entity = new MembershipRequestEntity
        {
            TenantId = request.TenantId,
            SubjectId = request.SubjectId,
            Message = request.Message,
            Status = MembershipRequestRecord.Pending,
            CreatedAt = request.CreatedAt,
        };
        lease.Context.MembershipRequests.Add(entity);
        await SaveAsync(lease.Context, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyPendingRequestAsync(Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.MembershipRequests
            .AnyAsync(r => r.TenantId == tenantId && r.SubjectId == subjectId && r.Status == MembershipRequestRecord.Pending, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<MembershipRequestRecord?> FindLatestRequestAsync(Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var request = await lease.Context.MembershipRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.SubjectId == subjectId)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return request is null ? null : ToRequest(request);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<MembershipRequestRecord>> ListPendingRequestsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var requests = await lease.Context.MembershipRequests.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Status == MembershipRequestRecord.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return requests.Select(ToRequest).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<MembershipRequestRecord?> GetRequestAsync(Guid tenantId, Guid requestId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var request = await lease.Context.MembershipRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == requestId, ct);
        return request is null ? null : ToRequest(request);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> DecideRequestAsync(Guid tenantId, Guid requestId, MembershipRequestDecision decision, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var roleIds = decision.RoleIds?.ToList();
        var decided = await lease.Context.MembershipRequests
            .Where(r => r.TenantId == tenantId && r.Id == requestId && r.Status == MembershipRequestRecord.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, decision.Status)
                .SetProperty(r => r.DecidedBySubjectId, decision.DecidedBySubjectId)
                .SetProperty(r => r.DecidedAt, decision.DecidedAt)
                .SetProperty(r => r.RoleIds, roleIds), ct);
        return decided == 1;
    }

    private static IReadOnlyList<TenantMemberRecord> ToMembers(List<TenantMemberEntity> members) =>
        members.Select(m => ToMember(m)!).ToList();

    private static TenantMemberRecord? ToMember(TenantMemberEntity? m) =>
        m is null ? null : new TenantMemberRecord(
            m.Id,
            m.TenantId,
            m.SubjectId,
            m.Subject?.Name,
            m.Subject?.Username,
            m.Subject?.IsActive ?? false,
            m.Subject?.IsSystemSubject ?? false,
            m.Subject?.IsDemoSubject ?? false,
            m.Subject?.IsPlatformAdmin ?? false,
            m.Subject?.EmailConfirmedAt is not null,
            m.MemberRoles
                .Where(mr => mr.TenantRole is not null)
                .Select(mr => new TenantMemberRoleRecord(mr.TenantRoleId, mr.TenantRole.Name, mr.TenantRole.Slug, [.. mr.TenantRole.Permissions]))
                .ToList(),
            m.DirectPermissions?.ToList(),
            m.Label,
            m.Username,
            m.LimitTo24Hours,
            m.AccessEndsAt,
            m.LastUsedAt,
            m.CreatedFromInviteId,
            m.SysCreatedAt);

    private static TenantRoleRecord ToRole(TenantRoleEntity role, int memberCount) => new(
        role.Id,
        role.TenantId,
        role.Name,
        role.Slug,
        role.Description,
        [.. role.Permissions],
        role.IsSystem,
        memberCount,
        role.SysCreatedAt);

    private static MembershipRequestRecord ToRequest(MembershipRequestEntity r) => new(
        r.Id,
        r.TenantId,
        r.SubjectId,
        r.Message,
        r.Status,
        r.DecidedBySubjectId,
        r.DecidedAt,
        r.RoleIds,
        r.CreatedAt);
}
