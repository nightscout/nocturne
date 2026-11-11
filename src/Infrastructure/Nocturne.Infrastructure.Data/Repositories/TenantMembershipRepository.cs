using Microsoft.EntityFrameworkCore;
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

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public TenantMembershipRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns and has
    /// pinned. A write is staged on the unit and not saved: the owner saves it, so a membership
    /// lands in one transaction with the tenant, roles or grants beside it.
    /// </summary>
    public TenantMembershipRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
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
        if (await db.TenantMembers.AnyAsync(m => m.TenantId == member.TenantId && m.SubjectId == member.SubjectId, ct))
            return false;

        var entity = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = member.TenantId,
            SubjectId = member.SubjectId,
            DirectPermissions = member.DirectPermissions?.ToList(),
            Label = member.Label,
            Username = member.Username,
            LimitTo24Hours = member.LimitTo24Hours,
            CreatedFromInviteId = member.CreatedFromInviteId,
        };
        db.TenantMembers.Add(entity);
        foreach (var roleId in member.RoleIds.Distinct())
        {
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = entity.Id,
                TenantRoleId = roleId,
            });
        }

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
        return member is null ? null : new TenantMemberAccess(member.EffectivePermissions().ToHashSet(), member.LimitTo24Hours);
    }

    /// <inheritdoc/>
    public virtual async Task<TenantMemberAccess?> GetMemberAccessAsync(Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var member = await Members(lease.Context)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
        return member is null ? null : new TenantMemberAccess(member.EffectivePermissions().ToHashSet(), member.LimitTo24Hours);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> IsMemberAsync(Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking()
            .AnyAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<string>> GetMemberRoleNamesAsync(Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        return await lease.Context.TenantMembers.AsNoTracking()
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
    public virtual async Task<IReadOnlyList<Guid>> GetOwnerSubjectIdsForAccessRequestAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        return await db.TenantMembers.AsNoTracking().OwnersOf(tenantId).Select(m => m.SubjectId).ToListAsync(ct);
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
    public virtual Task<OwnerGuardedChange> RemoveMemberUnlessLastOwnerAsync(Guid tenantId, Guid memberId, CancellationToken ct) =>
        UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            if (await Members(db).IsLastOwnerAsync(tenantId, memberId, token))
                return OwnerGuardedChange.LastOwner;

            var member = await db.TenantMembers
                .Include(m => m.MemberRoles)
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null)
                return OwnerGuardedChange.NotFound;

            db.TenantMemberRoles.RemoveRange(member.MemberRoles);
            db.TenantMembers.Remove(member);
            return OwnerGuardedChange.Changed;
        }, ct);

    /// <inheritdoc/>
    public virtual async Task<SubjectMembershipsRemoval> RemoveMembershipsOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        if (_unit is not null)
            return await RemoveMembershipsUnderOwnerLocksAsync(_unit.Context, subjectId, ct);

        await using var unit = await DbUnitOfWork.OpenPlatformAsync(Pinnable, ct);
        return await unit.CommitAsync(
            async token =>
            {
                var removal = await RemoveMembershipsUnderOwnerLocksAsync(unit.Context, subjectId, token);
                await unit.Context.SaveChangesAsync(token);
                return removal;
            },
            ct: ct);
    }

    private static async Task<SubjectMembershipsRemoval> RemoveMembershipsUnderOwnerLocksAsync(
        NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        var leftWithoutOwner = await db.LockTenantsOfSubjectAsync(subjectId, ct);
        var memberships = await db.TenantMembers
            .Include(m => m.MemberRoles)
            .Where(m => m.SubjectId == subjectId)
            .ToListAsync(ct);

        db.TenantMemberRoles.RemoveRange(memberships.SelectMany(m => m.MemberRoles));
        db.TenantMembers.RemoveRange(memberships);
        return new SubjectMembershipsRemoval(memberships.Count, leftWithoutOwner);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetDirectPermissionsAsync(
        Guid tenantId, Guid memberId, IReadOnlyList<string>? permissions, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var member = await lease.Context.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, ct);
        if (member is null)
            return false;

        member.DirectPermissions = permissions?.ToList();
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

        member.LimitTo24Hours = limitTo24Hours;
        member.SysUpdatedAt = now;
        await SaveAsync(lease.Context, ct);
        return true;
    }

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
                && await Members(db).IsLastOwnerAsync(tenantId, memberId, token))
                return OwnerGuardedChange.LastOwner;

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

        db.TenantMemberRoles.RemoveRange(member.MemberRoles.Where(mr => removedRoleIds.Contains(mr.TenantRoleId)));
        foreach (var roleId in addedRoleIds)
        {
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = roleId,
            });
        }

        member.SysUpdatedAt = now;
        return true;
    }

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
    public virtual async Task<SubjectMembershipRead?> ReadSubjectMembershipAsync(Guid subjectId, Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var db = lease.Context;
        var row = await db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .GroupJoin(
                db.TenantMembers.AsNoTracking().Where(m => m.TenantId == tenantId),
                s => s.Id,
                m => m.SubjectId,
                (s, members) => new { s, members })
            .SelectMany(
                joined => joined.members.DefaultIfEmpty(),
                (joined, m) => new MembershipRow
                {
                    IsPlatformAdmin = joined.s.IsPlatformAdmin,
                    MemberId = (Guid?)m!.Id,
                    DirectPermissions = m.DirectPermissions,
                    RolePermissions = m.MemberRoles.Select(mr => mr.TenantRole.Permissions).ToList(),
                    LimitTo24Hours = (bool?)m.LimitTo24Hours,
                    LastUsedAt = m.LastUsedAt,
                })
            .FirstOrDefaultAsync(ct);

        return row is null ? null : new SubjectMembershipRead(row.IsPlatformAdmin, row.Facts());
    }

    /// <summary>The subject's flag with the membership columns of the left join; <see cref="MemberId"/> is null when there is none.</summary>
    private sealed class MembershipRow
    {
        public bool IsPlatformAdmin { get; init; }

        public Guid? MemberId { get; init; }

        public List<string>? DirectPermissions { get; init; }

        public List<List<string>> RolePermissions { get; init; } = [];

        public bool? LimitTo24Hours { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public TenantMembershipFacts? Facts() => MemberId is { } id
            ? new TenantMembershipFacts(
                id,
                new TenantMemberAccess(
                    MemberPermissions.Effective(RolePermissions, DirectPermissions).ToHashSet(),
                    LimitTo24Hours == true),
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
    public virtual async Task<IReadOnlyList<TenantRecord>> ListTenantsOfSubjectAsync(Guid subjectId, bool activeOnly, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        var rows = await db.TenantMembers.AsNoTracking()
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
    public virtual async Task<IReadOnlyList<SubjectMembership>> ListMembershipsOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        var memberships = await db.TenantMembers.AsNoTracking()
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
    public virtual async Task<SetupGate?> ReadSetupGateAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        // One statement on every API request: no member row means no member can hold a credential.
        // Credentials and OIDC identities are subject-scoped, so both checks join through the members.
        var members = db.TenantMembers.Where(m => m.TenantId == tenantId);
        var orphans = db.OrphanedSubjectsOf(tenantId);
        var gate = await members
            .Take(1)
            .Select(_ => new
            {
                HasCredentials = members.Any(m =>
                    db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                    db.WorkingOidcIdentities().Any(i => i.SubjectId == m.SubjectId)),
                HasOrphans = orphans.Any(),
            })
            .FirstOrDefaultAsync(ct);
        return gate is null ? null : new SetupGate(gate.HasCredentials, gate.HasOrphans);
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
        return await db.OrphanedSubjectsOf(tenantId)
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
            .Include(r => r.MemberRoles)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, ct);
        if (role is null)
            return false;

        db.TenantMemberRoles.RemoveRange(role.MemberRoles);
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
            m.MemberRoles
                .Where(mr => mr.TenantRole is not null)
                .Select(mr => new TenantMemberRoleRecord(mr.TenantRoleId, mr.TenantRole.Name, mr.TenantRole.Slug, [.. mr.TenantRole.Permissions]))
                .ToList(),
            m.DirectPermissions?.ToList(),
            m.Label,
            m.Username,
            m.LimitTo24Hours,
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
