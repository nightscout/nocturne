using System.Linq.Expressions;
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

    /// <summary>
    /// Runs <paramref name="write"/> in a transaction, which a membership's lock needs: on this
    /// repository's unit, whose owner has opened it and commits it, else on a new unit pinned to
    /// <paramref name="tenantId"/>, committed here. The write saves through <see cref="SaveAsync"/>.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(
        Guid tenantId, Func<NocturneDbContext, CancellationToken, Task<T>> write, CancellationToken ct)
    {
        if (_unit is not null)
            return await write(_unit.Context, ct);

        await using var unit = await DbUnitOfWork.OpenForTenantAsync(Pinnable, tenantId, ct);
        return await unit.CommitAsync(token => write(unit.Context, token), ct: ct);
    }

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private IQueryable<TenantMemberEntity> Members(NocturneDbContext db) =>
        db.TenantMembers.AsNoTracking();

    private IQueryable<TenantMemberEntity> MembersWithDetails(NocturneDbContext db) =>
        WithReasons(Members(db)
            .Include(m => m.Subject)
            .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole));

    private static IQueryable<TenantMemberEntity> WithReasons(IQueryable<TenantMemberEntity> members) =>
        members
            .Include(m => m.Reasons).ThenInclude(r => r.ReasonRoles).ThenInclude(rr => rr.TenantRole)
            .Include(m => m.Reasons).ThenInclude(r => r.TeamAccess).ThenInclude(a => a!.Team);

    private IQueryable<TenantRoleEntity> Roles(NocturneDbContext db) =>
        db.TenantRoles.AsNoTracking();

    /// <inheritdoc/>
    public virtual async Task<bool> AddMemberAsync(NewTenantMember member, CancellationToken ct)
    {
        try
        {
            return await InTransactionAsync(member.TenantId, async (db, token) =>
            {
                await db.LockSubjectRowSharedAsync(member.SubjectId, token);
                await db.LockMembershipAsync(member.TenantId, member.SubjectId, token);
                if (await db.TenantMembers.AnyAsync(m => m.TenantId == member.TenantId && m.SubjectId == member.SubjectId, token))
                    return false;

                await StageAddAsync(db, member, token);
                await SaveAsync(db, token);
                return true;
            }, ct);
        }
        catch (DbUpdateException ex) when (_unit is null && ex.IsUniqueViolation())
        {
            // Another request made the subject a member between the check and the save.
            return false;
        }
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AddDirectMemberAsync(NewTenantMember member, DateTime now, CancellationToken ct)
    {
        try
        {
            return await InTransactionAsync(member.TenantId, async (db, token) =>
            {
                await db.LockSubjectRowSharedAsync(member.SubjectId, token);
                await db.LockMembershipAsync(member.TenantId, member.SubjectId, token);
                var existing = await db.TenantMembers
                    .FirstOrDefaultAsync(m => m.TenantId == member.TenantId && m.SubjectId == member.SubjectId, token);
                if (existing is null)
                    await StageAddAsync(db, member, token);
                else if (!await StageDirectReasonAsync(db, existing, member, now, token))
                    return false;

                await SaveAsync(db, token);
                return true;
            }, ct);
        }
        catch (DbUpdateException ex) when (_unit is null && ex.IsUniqueViolation())
        {
            // Another request let the subject in directly between the check and the save.
            return false;
        }
    }

    /// <summary>
    /// Stages a direct reason giving what <paramref name="member"/> names on <paramref name="existing"/>,
    /// with the audit rows, label and username a new member's would have; false, staging nothing,
    /// when it already holds one.
    /// </summary>
    private static async Task<bool> StageDirectReasonAsync(
        NocturneDbContext db, TenantMemberEntity existing, NewTenantMember member, DateTime now, CancellationToken ct)
    {
        if (await MembershipReasons.DirectGrantAsync(db, existing, now, ct) is not null)
            return false;

        var grant = GrantOf(member);
        var source = SourceOf(member);
        await MembershipReasons.AddDirectReasonAsync(
            db, existing, grant, member.CreatedFromInviteId, member.Label, member.Username, member.How, member.By, source, now, ct);
        await StageGrantRowsAsync(db, member.TenantId, member.SubjectId, grant, source, ct);
        existing.SysUpdatedAt = now;
        return true;
    }

    private static MembershipGrant GrantOf(NewTenantMember member) =>
        new(member.RoleIds.Distinct().ToList(), member.DirectPermissions, member.LimitTo24Hours, member.AccessEndsAt);

    private static MemberAccessSource? SourceOf(NewTenantMember member) => member.MadeBy switch
    {
        MembershipMadeBy.Member => new MemberAccessSource(member.SubjectId, member.CreatedFromInviteId),
        MembershipMadeBy.System => MemberAccessSource.System,
        _ => null,
    };

    /// <summary>
    /// Stages the rows of what <paramref name="grant"/> gives a member who had none of it: each role and
    /// direct permission given, the history limit and the end date.
    /// </summary>
    private static async Task StageGrantRowsAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, MembershipGrant grant, MemberAccessSource? source, CancellationToken ct)
    {
        MemberAccessAudit.StageRoles(db, tenantId, subjectId, await SlugsOfAsync(db, grant.RoleIds, ct), [], source);
        MemberAccessAudit.StagePermissions(db, tenantId, subjectId, null, grant.DirectPermissions, source);
        MemberAccessAudit.StageHistoryLimit(db, tenantId, subjectId, false, grant.LimitTo24Hours, source);
        MemberAccessAudit.StageAccessEnd(db, tenantId, subjectId, null, grant.AccessEndsAt, source);
    }

    /// <summary>
    /// Stages the rows of what <paramref name="grant"/> gave a member who keeps none of it: each role and
    /// direct permission taken. Its history limit is not, since it no longer applies to anything.
    /// </summary>
    private static async Task StageGrantRemovedRowsAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, MembershipGrant grant, MemberAccessSource? source, CancellationToken ct)
    {
        MemberAccessAudit.StageRoles(db, tenantId, subjectId, [], await SlugsOfAsync(db, grant.RoleIds, ct), source);
        MemberAccessAudit.StagePermissions(db, tenantId, subjectId, grant.DirectPermissions, null, source);
    }

    /// <summary>
    /// Stages the reason a team's access to the tenant (<paramref name="teamAccessId"/>) gives
    /// <paramref name="subjectId"/> through <see cref="MembershipReasons.AddTeamReasonAsync"/>, with the rows
    /// of what it gives, attributed to <paramref name="source"/>.
    /// </summary>
    internal static async Task<TenantMemberEntity> StageAddTeamReasonAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, Guid teamAccessId, MembershipGrant grant,
        MemberAccessSource source, DateTime now, CancellationToken ct)
    {
        var member = await MembershipReasons.AddTeamReasonAsync(db, tenantId, subjectId, teamAccessId, grant, source, now, ct);
        await StageGrantRowsAsync(db, tenantId, subjectId, grant, source, ct);
        return member;
    }

    /// <summary>
    /// Stages setting the reason a team's access to the tenant (<paramref name="teamAccessId"/>) gives
    /// <paramref name="subjectId"/> to <paramref name="grant"/> through <see cref="MembershipReasons.SetTeamReasonAsync"/>,
    /// with the rows of what changed in it, attributed to <paramref name="source"/>, on <paramref name="unit"/>,
    /// which must be pinned to <paramref name="tenantId"/>. A reason whose end had passed and whose grant is
    /// in force again also stages its joined row, as <see cref="StageAddTeamReasonAsync"/> does, since the
    /// member has access through the team again. A membership it leaves with an earlier end has what the
    /// subject made capped at that end (<see cref="MemberCreations.CapAsync"/>) on the same unit.
    /// </summary>
    /// <returns>The grants the cap lowered, for the caller to evict their sessions once the unit commits.</returns>
    /// <exception cref="InvalidOperationException">
    /// The subject holds no reason from the access, or <paramref name="grant"/> gives the owner role.
    /// </exception>
    internal static async Task<IReadOnlyList<OAuthGrantRecord>> StageSetTeamReasonAsync(
        DbUnitOfWork unit, Guid tenantId, Guid subjectId, Guid teamAccessId, MembershipGrant grant, MemberAccessSource source,
        DateTime now, CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockMembershipAsync(tenantId, subjectId, ct);
        var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
        if (member is null || await MembershipReasons.TeamGrantAsync(db, member, teamAccessId, now, ct) is not { } before)
            throw new InvalidOperationException($"Subject {subjectId} holds no reason from team access {teamAccessId} on tenant {tenantId}.");

        if (MembershipLiveness.HasEnded(before.AccessEndsAt, now) && !MembershipLiveness.HasEnded(grant.AccessEndsAt, now))
        {
            var team = await db.TeamTenantAccess.AsNoTracking()
                .Where(a => a.Id == teamAccessId)
                .Select(a => new MemberAccessTeam(a.TeamId, a.Team!.Name))
                .FirstAsync(ct);
            MemberAccessAudit.StageJoined(db, tenantId, subjectId, MembershipJoinHow.Team, inviteId: null, by: null, team, source);
        }

        MemberAccessAudit.StageRoles(
            db, tenantId, subjectId,
            await SlugsOfAsync(db, grant.RoleIds.Except(before.RoleIds).ToList(), ct),
            await SlugsOfAsync(db, before.RoleIds.Except(grant.RoleIds).ToList(), ct),
            source);
        MemberAccessAudit.StagePermissions(db, tenantId, subjectId, before.DirectPermissions, grant.DirectPermissions, source);
        MemberAccessAudit.StageHistoryLimit(db, tenantId, subjectId, before.LimitTo24Hours, grant.LimitTo24Hours, source);
        MemberAccessAudit.StageAccessEnd(db, tenantId, subjectId, before.AccessEndsAt, grant.AccessEndsAt, source);
        return await MembershipReasons.SetTeamReasonAsync(db, member, teamAccessId, grant, now, ct) is { } end
            ? await MemberCreations.CapAsync(unit, subjectId, end, now, ct)
            : [];
    }

    /// <summary>
    /// Stages the removal of the reason a team's access to the tenant (<paramref name="teamAccessId"/>)
    /// gives <paramref name="subjectId"/> through <see cref="MembershipReasons.RemoveTeamReasonAsync"/>, with
    /// the rows of what it gave, attributed to <paramref name="source"/>, on <paramref name="unit"/>, which
    /// must be pinned to <paramref name="tenantId"/>. A membership it leaves with an earlier end has what
    /// the subject made capped at that end (<see cref="MemberCreations.CapAsync"/>) on the same unit.
    /// When it is the member's only reason, the membership is removed and nothing they made is revoked
    /// or capped here: the caller must revoke and cap what the member made, as <c>MembershipRemoval</c>
    /// does for a direct removal.
    /// </summary>
    /// <returns>
    /// The grants the cap lowered, for the caller to evict their sessions once the unit commits; null
    /// when they hold no such reason.
    /// </returns>
    internal static async Task<IReadOnlyList<OAuthGrantRecord>?> StageRemoveTeamReasonAsync(
        DbUnitOfWork unit, Guid tenantId, Guid subjectId, Guid teamAccessId, MemberAccessSource source, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        await db.LockMembershipAsync(tenantId, subjectId, ct);
        var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
        if (member is null || await MembershipReasons.TeamGrantAsync(db, member, teamAccessId, now, ct) is not { } grant)
            return null;

        await StageGrantRemovedRowsAsync(db, tenantId, subjectId, grant, source, ct);
        return await MembershipReasons.RemoveTeamReasonAsync(db, member, teamAccessId, source, now, ct) is { } end
            ? await MemberCreations.CapAsync(unit, subjectId, end, now, ct)
            : [];
    }

    /// <summary>
    /// Stages <paramref name="member"/> on <paramref name="db"/> through <see cref="MembershipReasons"/>,
    /// with the audit rows of what it is given, attributed as its <see cref="NewTenantMember.MadeBy"/> says.
    /// The caller holds the membership's lock (<see cref="MembershipLock"/>) from its check that none exists.
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
        var grant = GrantOf(member);
        var source = SourceOf(member);
        MembershipReasons.AddMember(db, entity, grant, member.CreatedFromInviteId, member.How, member.By, source);
        await StageGrantRowsAsync(db, member.TenantId, member.SubjectId, grant, source, ct);
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
    public virtual Task<TenantMemberAccess?> GetMemberAccessAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        MembershipReasons.ReadInForceAsync(_contexts, tenantId, now, async (inForce, token) =>
        {
            await using var lease = await OpenForTenantAsync(tenantId, token);
            var member = await Members(lease.Context)
                .Where(inForce)
                .Include(m => m.MemberRoles).ThenInclude(mr => mr.TenantRole)
                .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, token);
            return (ToAccess(member), subjectId, member?.NextReasonLapseAt);
        }, ct);

    private static TenantMemberAccess? ToAccess(TenantMemberEntity? member) =>
        member is null
            ? null
            : new TenantMemberAccess(member.EffectivePermissions().ToHashSet(), member.LimitTo24Hours, member.AccessEndsAt);

    /// <inheritdoc/>
    public virtual async Task<bool> IsDirectMemberAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        await FindMemberAsync(tenantId, subjectId, ct) is { DirectReason: { } direct }
        && !MembershipLiveness.HasEnded(direct.AccessEndsAt, now);

    /// <inheritdoc/>
    public virtual Task<bool> IsMemberAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        MembershipReasons.ReadInForceAsync(_contexts, tenantId, now, async (inForce, token) =>
        {
            await using var lease = await OpenForTenantAsync(tenantId, token);
            var member = await lease.Context.TenantMembers.AsNoTracking()
                .Where(inForce)
                .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
                .Select(m => new { m.NextReasonLapseAt })
                .FirstOrDefaultAsync(token);
            return (member is not null, subjectId, member?.NextReasonLapseAt);
        }, ct);

    /// <inheritdoc/>
    public virtual Task<IReadOnlyList<string>> GetMemberRoleNamesAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        MembershipReasons.ReadInForceAsync(_contexts, tenantId, now, async (inForce, token) =>
        {
            await using var lease = await OpenForTenantAsync(tenantId, token);
            var member = await lease.Context.TenantMembers.AsNoTracking()
                .Where(inForce)
                .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
                .Select(m => new { m.Id, m.NextReasonLapseAt })
                .FirstOrDefaultAsync(token);
            if (member is null)
                return ((IReadOnlyList<string>)[], subjectId, null);

            var names = await lease.Context.TenantMemberRoles.AsNoTracking()
                .Where(mr => mr.TenantMemberId == member.Id)
                .Select(mr => mr.TenantRole.Name)
                .Distinct()
                .ToListAsync(token);
            return ((IReadOnlyList<string>)names, subjectId, member.NextReasonLapseAt);
        }, ct);

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
    public virtual async Task<IReadOnlyList<Guid>> GetOwnerSubjectIdsWhoCanSignInAsync(Guid tenantId, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var db = lease.Context;
        var capable = db.Subjects.WhoCanSignIn(db, _linkRoute);
        return await db.TenantMembers.AsNoTracking()
            .OwnersOf(tenantId)
            .Where(m => capable.Any(s => s.Id == m.SubjectId))
            .Select(m => m.SubjectId)
            .ToListAsync(ct);
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
    /// Stages <paramref name="change"/> on this repository's unit under the tenant's owner lock
    /// (<see cref="TenantOwnerLock"/>) and its team access lock (<see cref="TeamAccessLock"/>), in the
    /// order <see cref="MembershipLock"/> states, for the unit's owner to save, sync and commit.
    /// </summary>
    /// <remarks>
    /// Holding the owner lock from the check to the commit makes the second of two owners stepping down
    /// or removing each other read the first one's write. A change here can change whether the member
    /// owns the site, and an owner holds no team reason, so the owner of the unit syncs the member's team
    /// reasons in the same commit (<see cref="TeamReasonChanges.SyncAsync"/>), which needs the team access
    /// lock; it cannot be taken after the membership lock the change takes. There is no form without a
    /// unit: it would commit an owner change that nothing syncs.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The repository is not over a unit, or the unit has no transaction open.
    /// </exception>
    private async Task<OwnerGuardedChange> UnderOwnersLockAsync(
        Guid tenantId,
        Func<NocturneDbContext, CancellationToken, Task<OwnerGuardedChange>> change,
        CancellationToken ct)
    {
        var unit = _unit ?? throw new InvalidOperationException(
            "An owner-guarded change runs on a repository over a unit, whose owner syncs the member's team reasons in its commit.");
        await unit.Context.LockTenantOwnersAsync(tenantId, ct);
        await unit.Context.LockTeamAccessAsync(tenantId, ct);
        return await change(unit.Context, ct);
    }

    /// <summary>
    /// Takes, on <paramref name="unit"/>, what a write that can change whether <paramref name="subjectId"/>
    /// owns <paramref name="tenantId"/> holds from before any other lock to its commit, in the order
    /// <see cref="MembershipLock"/> states: the subject's row, the site's owners lock and its team access
    /// lock. Under them the write and then a sync of the subject's team reasons at the site
    /// (<see cref="TeamReasonChanges.SyncAsync"/>) can each take the membership's lock.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transaction is open on the unit.</exception>
    public static async Task LockOwnerChangeAsync(DbUnitOfWork unit, Guid tenantId, Guid subjectId, CancellationToken ct)
    {
        await unit.LockSubjectRowSharedAsync(subjectId, ct);
        await unit.Context.LockTenantOwnersAsync(tenantId, ct);
        await unit.Context.LockTeamAccessAsync(tenantId, ct);
    }

    /// <summary>
    /// Stages on this repository's unit the removal of the membership's direct reason, refused with
    /// <see cref="OwnerGuardedChange.LastOwner"/> when <see cref="TenantOwnerFilter.IsLastOwnerAsync"/>
    /// says it is the last owner, and with <see cref="OwnerGuardedChange.FromTeam"/> when it holds no
    /// direct reason. A membership whose only reason it is goes with it; one that also holds a team's
    /// reason stays, projected at <paramref name="now"/> from what is left, and says the end it keeps.
    /// Its removed row records <paramref name="how"/>.
    /// </summary>
    /// <remarks>
    /// Call it inside the unit's commit, which holds the locks <see cref="UnderOwnersLockAsync"/> takes
    /// until it commits. The caller saves, caps what the member made at
    /// <see cref="DirectReasonRemoval.KeptAccessEndsAt"/> (<c>MemberCreations.CapAsync</c>) and syncs the
    /// subject's team reasons on the tenant, all in that commit.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The repository is not over a unit.</exception>
    public async Task<DirectReasonRemoval> RemoveMemberUnlessLastOwnerAsync(
        Guid tenantId, Guid memberId, MembershipEndHow how, DateTime now, CancellationToken ct)
    {
        var kept = false;
        DateTime? keptEnd = null;
        var outcome = await UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            (kept, keptEnd) = (false, null);
            if (await db.IsLastOwnerAsync(tenantId, memberId, _linkRoute, token))
                return OwnerGuardedChange.LastOwner;

            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null)
                return OwnerGuardedChange.NotFound;
            if (await MembershipReasons.DirectGrantAsync(db, member, now, token) is not { } direct)
                return OwnerGuardedChange.FromTeam;

            await StageGrantRemovedRowsAsync(db, tenantId, member.SubjectId, direct, source: null, token);
            kept = await MembershipReasons.RemoveDirectReasonAsync(db, member, how, now, token);
            keptEnd = kept ? member.AccessEndsAt : null;
            return OwnerGuardedChange.Changed;
        }, ct);
        return new DirectReasonRemoval(outcome, kept, keptEnd);
    }

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
        await db.LockTeamManagersOfSubjectAsync(subjectId, ct);
        var memberships = await db.TenantMembers
            .Include(m => m.MemberRoles)
            .Where(m => m.SubjectId == subjectId)
            .ToListAsync(ct);
        await db.LockMembershipsAsync(memberships.Select(m => (m.TenantId, m.SubjectId)), ct);

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
    public virtual Task<bool> SetDirectPermissionsAsync(
        Guid tenantId, Guid memberId, IReadOnlyList<string>? permissions, DateTime now, CancellationToken ct) =>
        InTransactionAsync(tenantId, async (db, token) =>
        {
            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null || await MembershipReasons.DirectGrantAsync(db, member, now, token) is not { } direct)
                return false;

            MemberAccessAudit.StagePermissions(db, tenantId, member.SubjectId, direct.DirectPermissions, permissions);
            await MembershipReasons.SetDirectPermissionsAsync(db, member, permissions, now, token);
            member.SysUpdatedAt = now;
            await SaveAsync(db, token);
            return true;
        }, ct);

    /// <inheritdoc/>
    public virtual Task<bool> SetLimitTo24HoursAsync(Guid tenantId, Guid memberId, bool limitTo24Hours, DateTime now, CancellationToken ct) =>
        InTransactionAsync(tenantId, async (db, token) =>
        {
            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null || await MembershipReasons.DirectGrantAsync(db, member, now, token) is not { } direct)
                return false;

            MemberAccessAudit.StageHistoryLimit(db, tenantId, member.SubjectId, direct.LimitTo24Hours, limitTo24Hours);
            await MembershipReasons.SetLimitTo24HoursAsync(db, member, limitTo24Hours, now, token);
            member.SysUpdatedAt = now;
            await SaveAsync(db, token);
            return true;
        }, ct);

    /// <summary>
    /// Stages on this repository's unit when the membership's direct reason ends, null clearing it, and
    /// stamps it updated at <paramref name="now"/> when the date moved. Refused with
    /// <see cref="OwnerGuardedChange.OwnerAccessEnds"/> when it would give an owner an end date, and with
    /// <see cref="OwnerGuardedChange.FromTeam"/> when it holds no direct reason. The caller saves, caps
    /// what the member made and syncs the member's team reasons in the unit's commit.
    /// </summary>
    /// <remarks>Holds the locks <see cref="UnderOwnersLockAsync"/> takes, so it and a role change cannot race.</remarks>
    /// <exception cref="InvalidOperationException">The repository is not over a unit.</exception>
    public Task<OwnerGuardedChange> SetAccessEndUnlessOwnerAsync(
        Guid tenantId, Guid memberId, DateTime? accessEndsAt, DateTime now, CancellationToken ct) =>
        UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null)
                return OwnerGuardedChange.NotFound;
            if (await MembershipReasons.DirectGrantAsync(db, member, now, token) is not { } direct)
                return OwnerGuardedChange.FromTeam;

            if (accessEndsAt is not null && await GivesOwnerAsync(db, tenantId, direct.RoleIds, token))
                return OwnerGuardedChange.OwnerAccessEnds;

            MemberAccessAudit.StageAccessEnd(db, tenantId, member.SubjectId, direct.AccessEndsAt, accessEndsAt);
            if (direct.AccessEndsAt != accessEndsAt)
            {
                await MembershipReasons.SetAccessEndAsync(db, member, accessEndsAt, now, token);
                member.SysUpdatedAt = now;
            }

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
    public virtual Task<bool> ReplaceRolesAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct) =>
        InTransactionAsync(tenantId, async (db, token) =>
        {
            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null || await MembershipReasons.DirectGrantAsync(db, member, now, token) is not { } direct)
                return false;

            await StageRoleReplacementAsync(db, member, direct, removedRoleIds, addedRoleIds, now, token);
            await SaveAsync(db, token);
            return true;
        }, ct);

    /// <summary>
    /// <see cref="ReplaceRolesAsync"/> staged on this repository's unit, refused with
    /// <see cref="OwnerGuardedChange.LastOwner"/> when it removes the owner role from a membership
    /// <see cref="TenantOwnerFilter.IsLastOwnerAsync"/> says is the last owner, with
    /// <see cref="OwnerGuardedChange.OwnerAccessEnds"/> when it gives the owner role to a direct reason
    /// that has an end date, and with <see cref="OwnerGuardedChange.FromTeam"/> when the membership holds
    /// no direct reason. The caller saves and syncs the member's team reasons in the unit's commit.
    /// </summary>
    /// <remarks>Holds the locks <see cref="UnderOwnersLockAsync"/> takes.</remarks>
    /// <exception cref="InvalidOperationException">The repository is not over a unit.</exception>
    public Task<OwnerGuardedChange> ReplaceRolesUnlessLastOwnerAsync(
        Guid tenantId, Guid memberId, IReadOnlyCollection<Guid> removedRoleIds, IReadOnlyCollection<Guid> addedRoleIds,
        DateTime now, CancellationToken ct) =>
        UnderOwnersLockAsync(tenantId, async (db, token) =>
        {
            if (await GivesOwnerAsync(db, tenantId, removedRoleIds, token)
                && await db.IsLastOwnerAsync(tenantId, memberId, _linkRoute, token))
                return OwnerGuardedChange.LastOwner;

            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.Id == memberId, token);
            if (member is null)
                return OwnerGuardedChange.NotFound;
            if (await MembershipReasons.DirectGrantAsync(db, member, now, token) is not { } direct)
                return OwnerGuardedChange.FromTeam;

            if (await GivesOwnerAsync(db, tenantId, addedRoleIds, token) && direct.AccessEndsAt is not null)
                return OwnerGuardedChange.OwnerAccessEnds;

            await StageRoleReplacementAsync(db, member, direct, removedRoleIds, addedRoleIds, now, token);
            return OwnerGuardedChange.Changed;
        }, ct);

    private static Task<bool> GivesOwnerAsync(NocturneDbContext db, Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct) =>
        db.TenantRoles.AsNoTracking().AnyAsync(r => r.TenantId == tenantId && r.Slug == RoleSeeds.Owner && roleIds.Contains(r.Id), ct);

    /// <summary>Stages the replacement of the roles <paramref name="direct"/>, the member's direct reason, gives.</summary>
    private static async Task StageRoleReplacementAsync(
        NocturneDbContext db, TenantMemberEntity member, MembershipGrant direct, IReadOnlyCollection<Guid> removedRoleIds,
        IReadOnlyCollection<Guid> addedRoleIds, DateTime now, CancellationToken ct)
    {
        var before = direct.RoleIds.ToHashSet();
        var after = before.Where(id => !removedRoleIds.Contains(id)).Concat(addedRoleIds).ToHashSet();
        MemberAccessAudit.StageRoles(
            db, member.TenantId, member.SubjectId,
            await SlugsOfAsync(db, after.Except(before).ToList(), ct),
            await SlugsOfAsync(db, before.Except(after).ToList(), ct));

        await MembershipReasons.ReplaceRolesAsync(db, member, removedRoleIds, addedRoleIds, now, ct);
        member.SysUpdatedAt = now;
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
    public virtual Task<SubjectMembershipRead?> ReadSubjectMembershipAsync(
        Guid subjectId, Guid tenantId, DateTime now, CancellationToken ct) =>
        MembershipReasons.ReadInForceAsync(_contexts, tenantId, now, async (inForce, token) =>
        {
            var row = await ReadSubjectMembershipRowAsync(subjectId, tenantId, now, inForce, token);
            return (row is null ? null : new SubjectMembershipRead(row.IsPlatformAdmin, row.IsActive, row.Facts(now)), subjectId, row?.NextReasonLapseAt);
        }, ct);

    private async Task<MembershipRow?> ReadSubjectMembershipRowAsync(
        Guid subjectId, Guid tenantId, DateTime now, Expression<Func<TenantMemberEntity, bool>> inForce, CancellationToken ct)
    {
        await using var lease = await OpenForTenantAsync(tenantId, ct);
        var db = lease.Context;
        return await db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .GroupJoin(
                db.TenantMembers.AsNoTracking().Where(inForce).Where(m => m.TenantId == tenantId),
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
                    NextReasonLapseAt = m.NextReasonLapseAt,
                    LastUsedAt = m.LastUsedAt,
                    Reasons = m.Reasons.AsQueryable()
                        .Where(r => r.TenantMember.NextReasonLapseAt == null || r.TenantMember.NextReasonLapseAt > now)
                        .Select(MembershipReasonColumns.Of).ToList(),
                })
            .FirstOrDefaultAsync(ct);
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

        public DateTime? NextReasonLapseAt { get; init; }

        public DateTime? LastUsedAt { get; init; }

        public List<MembershipReasonColumns> Reasons { get; init; } = [];

        public TenantMembershipFacts? Facts(DateTime now)
        {
            if (MemberId is not { } id)
                return null;

            var access = new TenantMemberAccess(
                MemberPermissions.Effective(RolePermissions, DirectPermissions).ToHashSet(),
                LimitTo24Hours == true,
                AccessEndsAt);
            return new TenantMembershipFacts(id, access, MembershipReasonColumns.DirectAccess(Reasons, access, now), LastUsedAt);
        }
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> ListTenantIdsOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        return await db.TenantMembers.AsNoTracking().TenantIdsOf(subjectId).ToListAsync(ct);
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

    /// <summary>
    /// Applies <paramref name="edit"/> to the role on <paramref name="unit"/>, which must be pinned to
    /// <paramref name="tenantId"/>, stamps it updated at <paramref name="now"/> and saves it. Returns with
    /// it the team reasons that bring the memberships of the staff of every team whose access gives the role
    /// into line (<see cref="TeamReasonChanges.SyncAsync"/>): a role id resolves at read time, but what a
    /// ceiling lets through of a role is a copy of its permissions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While a team's access gives the role, an edit that is not data only (<see cref="Scope.IsDataOnly"/>)
    /// is refused with <see cref="RoleChangeOutcome.HeldByTeam"/>, and one that adds a permission the role
    /// does not hold is refused with <see cref="RoleChangeOutcome.HeldByTeamOwnerOnly"/> unless
    /// <paramref name="editorSubjectId"/> is an owner of the site in force: only an owner may give a team
    /// more. A null <paramref name="editorSubjectId"/> is the system, which is no owner.
    /// </para>
    /// <para>
    /// It locks the rows of the staff it may sync, then the site's owners lock and team access lock
    /// (<see cref="MembershipLock"/>), so an approval, a join or an owner change racing it is either seen or
    /// waits. Null, writing nothing, when a staff member joined between the two: the caller runs it again.
    /// </para>
    /// </remarks>
    public static async Task<(TenantRoleUpdate Update, TeamReasonChanges? Changes)?> LockRoleEditAsync(
        DbUnitOfWork unit, Guid tenantId, Guid roleId, TenantRoleEdit edit, Guid? editorSubjectId, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        var locked = (await StaffOfRoleAsync(db, tenantId, roleId, ct)).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTenantOwnersAsync(tenantId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        var staff = await StaffOfRoleAsync(db, tenantId, roleId, ct);
        if (!locked.IsSupersetOf(staff))
            return null;

        var (outcome, updated) = await StageRoleEditAsync(db, tenantId, roleId, edit, editorSubjectId, now, ct);
        if (outcome != RoleChangeOutcome.Changed)
            return (new TenantRoleUpdate(outcome, null), null);

        await db.SaveChangesAsync(ct);
        return (new TenantRoleUpdate(outcome, updated), await TeamReasonChanges.SyncAsync(
            unit, staff.Select(subjectId => (tenantId, subjectId)), now, ct));
    }

    /// <summary>
    /// The subjects a team reason from an access of <paramref name="tenantId"/> that gives the role can
    /// reach: the staff of each such access's team, and whoever holds a reason from one.
    /// </summary>
    private static Task<List<Guid>> StaffOfRoleAsync(NocturneDbContext db, Guid tenantId, Guid roleId, CancellationToken ct)
    {
        var accessIds = db.TeamTenantAccessRoles.AsNoTracking()
            .Where(r => r.TenantRoleId == roleId && r.TeamTenantAccess!.TenantId == tenantId)
            .Select(r => r.TeamTenantAccessId);
        return db.TeamSiteStaff.AsNoTracking()
            .Where(s => accessIds.Contains(s.TeamTenantAccessId))
            .Select(s => s.TeamMember!.SubjectId)
            .Union(db.TenantMemberReasons.AsNoTracking()
                .Where(r => r.Kind == MembershipReasonKind.Team && r.SourceId != null && accessIds.Contains(r.SourceId.Value))
                .Select(r => r.TenantMember.SubjectId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stages <paramref name="edit"/> on the role, refused as <see cref="LockRoleEditAsync"/> says while a
    /// team's access gives it. The caller holds the site's owners lock and team access lock.
    /// </summary>
    private static async Task<(RoleChangeOutcome Outcome, TenantRoleRecord? Role)> StageRoleEditAsync(
        NocturneDbContext db, Guid tenantId, Guid roleId, TenantRoleEdit edit, Guid? editorSubjectId, DateTime now,
        CancellationToken ct)
    {
        var entity = await db.TenantRoles
            .Include(r => r.MemberRoles)
            .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, ct);
        if (entity is null)
            return (RoleChangeOutcome.NotFound, null);
        if (await HeldByTeamAsync(db, tenantId, roleId, ct))
        {
            if (!Scope.IsDataOnly(edit.Permissions))
                return (RoleChangeOutcome.HeldByTeam, null);
            if (Scope.ValidateGrant(edit.Permissions, entity.Permissions) is not null
                && !(editorSubjectId is { } editor && await IsOwnerInForceAsync(db, tenantId, editor, now, ct)))
                return (RoleChangeOutcome.HeldByTeamOwnerOnly, null);
        }

        MemberAccessAudit.StageRolePermissions(db, tenantId, roleId, edit.Slug, entity.Permissions, edit.Permissions);
        entity.Name = edit.Name;
        entity.Slug = edit.Slug;
        entity.Description = edit.Description;
        entity.Permissions = [.. edit.Permissions];
        entity.SysUpdatedAt = now;
        return (RoleChangeOutcome.Changed, ToRole(entity, entity.MemberRoles.Count));
    }

    /// <inheritdoc/>
    public virtual Task<RoleChangeOutcome> DeleteRoleAsync(Guid tenantId, Guid roleId, DateTime now, CancellationToken ct) =>
        UnderTeamAccessLockAsync(tenantId, async (db, token) =>
        {
            var role = await db.TenantRoles
                .Include(r => r.MemberRoles).ThenInclude(mr => mr.TenantMember)
                .FirstOrDefaultAsync(r => r.Id == roleId && r.TenantId == tenantId, token);
            if (role is null)
                return RoleChangeOutcome.NotFound;
            if (await HeldByTeamAsync(db, tenantId, roleId, token))
                return RoleChangeOutcome.HeldByTeam;

            foreach (var holder in role.MemberRoles)
                MemberAccessAudit.StageRoles(db, tenantId, holder.TenantMember.SubjectId, [], [role.Slug]);
            await MembershipReasons.RemoveRoleAsync(db, role.Id, now, token);
            db.TenantRoles.Remove(role);
            return RoleChangeOutcome.Changed;
        }, ct);

    private static Task<bool> HeldByTeamAsync(NocturneDbContext db, Guid tenantId, Guid roleId, CancellationToken ct) =>
        TeamRepository.RolesHeldByTeams(db, tenantId).AnyAsync(id => id == roleId, ct);

    private static Task<bool> IsOwnerInForceAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        db.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .AnyAsync(m => m.TenantId == tenantId
                && m.SubjectId == subjectId
                && m.MemberRoles.Any(r => r.TenantRole!.Slug == RoleSeeds.Owner), ct);

    /// <summary>
    /// Runs <paramref name="change"/> under the tenant's owners lock and team access lock, in the order
    /// <see cref="MembershipLock"/> states, and saves it when it reports
    /// <see cref="RoleChangeOutcome.Changed"/>: on this repository's unit, whose owner commits it, else
    /// on a new unit pinned to <paramref name="tenantId"/>.
    /// </summary>
    private async Task<RoleChangeOutcome> UnderTeamAccessLockAsync(
        Guid tenantId,
        Func<NocturneDbContext, CancellationToken, Task<RoleChangeOutcome>> change,
        CancellationToken ct)
    {
        if (_unit is not null)
        {
            await _unit.Context.LockTenantOwnersAsync(tenantId, ct);
            await _unit.Context.LockTeamAccessAsync(tenantId, ct);
            return await change(_unit.Context, ct);
        }

        await using var unit = await DbUnitOfWork.OpenForTenantAsync(Pinnable, tenantId, ct);
        return await unit.CommitAsync(
            async token =>
            {
                await unit.Context.LockTenantOwnersAsync(tenantId, token);
                await unit.Context.LockTeamAccessAsync(tenantId, token);
                var outcome = await change(unit.Context, token);
                if (outcome == RoleChangeOutcome.Changed)
                    await unit.Context.SaveChangesAsync(token);
                return outcome;
            },
            ct: ct);
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
            m.SysCreatedAt,
            m.Reasons
                .OrderBy(r => r.Kind != MembershipReasonKind.Direct)
                .ThenBy(r => r.Id)
                .Select(r => new TenantMemberReasonRecord(
                    r.Kind,
                    r is { Kind: MembershipReasonKind.Team, TeamAccess.Team: { } team } && r.TeamAccess.TenantId == m.TenantId
                        ? new MemberReasonTeam(team.Id, team.Name)
                        : null,
                    r.ReasonRoles
                        .Where(rr => rr.TenantRole is not null)
                        .Select(rr => new TenantMemberRoleRecord(rr.TenantRoleId, rr.TenantRole.Name, rr.TenantRole.Slug, [.. rr.TenantRole.Permissions]))
                        .ToList(),
                    r.DirectPermissions?.ToList(),
                    r.LimitTo24Hours,
                    r.AccessEndsAt))
                .ToList());

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
