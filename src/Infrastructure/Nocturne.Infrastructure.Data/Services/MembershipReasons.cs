using System.Data.Common;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Services;

/// <summary>
/// The one writer of a membership's access: it alone adds or removes a <c>tenant_members</c> row or
/// its role links, and sets the row's direct permissions, 24-hour limit, end date, next lapse and
/// invite. Each change is made to a reason (<c>tenant_member_reasons</c>), after which the row and its
/// role links are projected to the sum of the member's reasons in force
/// (<see cref="MembershipSum.InForceAt"/>), on the same context, so one save commits both.
/// </summary>
/// <remarks>
/// <para>
/// The row records when the next of its reasons lapses. From then it is due, no reader takes it as in
/// force (<see cref="MembershipLiveness"/>), and <see cref="RecomputeAsync(NocturneDbContext, Guid, Guid, DateTime, CancellationToken)"/>
/// projects it again: on the reads that decide a request or a sign-in (<see cref="ReadInForceAsync{T}"/>),
/// in the sweep (<see cref="RecomputeDueAsync"/>), and at startup.
/// </para>
/// <para>
/// A membership's start and end are audited here, so no way of adding or removing one goes
/// unrecorded: <see cref="AddMember"/> stages its <see cref="Nocturne.Core.Contracts.Auth.AuthAuditEventType.MemberJoined"/>
/// row and <see cref="RemoveMemberAsync"/> its <see cref="Nocturne.Core.Contracts.Auth.AuthAuditEventType.MemberRemoved"/>
/// row. So are a reason's start and end while the membership holds another: adding a direct reason
/// beside a team's, removing the direct reason while a team's remains, and adding or removing a team's
/// reason each stage the joined or removed row a membership starting or ending with it would.
/// The rows of what changed within it are not staged here: <see cref="MemberAccessAudit"/> stages
/// them from the change the caller asked for.
/// </para>
/// <para>
/// Before a change, the row and its reasons are brought together by <see cref="AlignWithRow"/>: a row
/// with no reason (written by an older build, or seeded by a test) is adopted, and a direct reason the
/// row no longer matches is re-synced from the row, so the change starts from what the row gives.
/// </para>
/// <para>
/// The row matches its reasons when it holds the same set of roles, the same direct permissions
/// exactly (null against empty, order and repeats all count, because the row stores the list as the
/// reasons give it), the same limit, end and next lapse, and the direct reason's invite. A projection
/// that finds the row matching writes nothing.
/// </para>
/// <para>
/// Every path that reads a member's reasons to change them takes the member's
/// <see cref="MembershipLock"/> first, so it needs a transaction open on the context. Adding a member
/// reads no reasons: the caller that checks no membership exists holds the lock from that check.
/// </para>
/// <para>
/// Postgres removes a member's reasons by cascade, but removal also removes them explicitly, so a
/// provider that does not cascade to untracked rows leaves none behind.
/// </para>
/// </remarks>
internal static class MembershipReasons
{
    /// <summary>
    /// Adds <paramref name="member"/>, which carries only its row-only state (ids, label, username,
    /// last use), with a direct reason that gives <paramref name="direct"/>, and stages its joined row
    /// attributed to <paramref name="source"/>, or the scope's actor when it is null.
    /// </summary>
    public static void AddMember(
        NocturneDbContext db, TenantMemberEntity member, MembershipGrant direct, Guid? createdFromInviteId,
        MembershipJoinHow how, Guid? by, MemberAccessSource? source)
    {
        MemberAccessAudit.StageJoined(db, member.TenantId, member.SubjectId, how, createdFromInviteId, by, team: null, source);
        db.TenantMembers.Add(member);
        var reason = Described(db, NewReason(db, member.Id, MembershipReasonKind.Direct, sourceId: null, direct, createdFromInviteId));
        // Its one reason is the sum at any instant, and nothing can lapse before it.
        Write(db, member, new MembershipProjection(reason.Grant, NextReasonLapseAt: null), [reason]);
    }

    /// <summary>
    /// Gives <paramref name="member"/>, which holds only reasons of other kinds, a direct reason that
    /// gives <paramref name="direct"/>, and stages its joined row as <see cref="AddMember"/> does for a
    /// new member. The row takes <paramref name="label"/> and <paramref name="username"/> as a new
    /// member's would; one that is null leaves the row's, since a username is what a passkey sign-in
    /// by name finds the member by.
    /// </summary>
    /// <exception cref="InvalidOperationException">The member already holds a direct reason.</exception>
    public static async Task AddDirectReasonAsync(
        NocturneDbContext db, TenantMemberEntity member, MembershipGrant direct, Guid? createdFromInviteId,
        string? label, string? username, MembershipJoinHow how, Guid? by, MemberAccessSource? source, DateTime now,
        CancellationToken ct)
    {
        if ((await AlignedAsync(db, member, now, ct)).Any(r => r.Kind == MembershipReasonKind.Direct))
            throw new InvalidOperationException($"Member {member.Id} already holds a direct reason.");

        member.Label = label ?? member.Label;
        member.Username = username ?? member.Username;
        MemberAccessAudit.StageJoined(db, member.TenantId, member.SubjectId, how, createdFromInviteId, by, team: null, source);
        NewReason(db, member.Id, MembershipReasonKind.Direct, sourceId: null, direct, createdFromInviteId);
        Project(db, member, now);
    }

    /// <summary>
    /// What the member's direct reason gives once <see cref="AlignWithRow"/> has brought it into line
    /// with the row; null when the member holds no direct reason. Takes the member's lock.
    /// </summary>
    public static async Task<MembershipGrant?> DirectGrantAsync(
        NocturneDbContext db, TenantMemberEntity member, DateTime now, CancellationToken ct) =>
        (await AlignedAsync(db, member, now, ct)).FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct) is { } direct
            ? Described(db, direct).Grant
            : null;

    /// <summary>
    /// Removes the member's direct reason and stages its removed row naming <paramref name="how"/>,
    /// attributed to the scope's actor. A member whose only reason it is is removed with it
    /// (<see cref="RemoveMemberAsync"/>); any other keeps its row, projected from what is left.
    /// </summary>
    /// <returns>Whether the member keeps its row for another reason.</returns>
    /// <exception cref="InvalidOperationException">The member holds no direct reason.</exception>
    public static async Task<bool> RemoveDirectReasonAsync(
        NocturneDbContext db, TenantMemberEntity member, MembershipEndHow how, DateTime now, CancellationToken ct)
    {
        var reasons = await AlignedAsync(db, member, now, ct);
        var direct = reasons.FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct)
            ?? throw new InvalidOperationException($"Member {member.Id} has no direct reason to remove.");
        if (reasons.Count == 1)
        {
            await RemoveMemberAsync(db, member, how, ct);
            return false;
        }

        MemberAccessAudit.StageRemoved(db, member.TenantId, member.SubjectId, how, team: null, source: null);
        RemoveReason(db, direct);
        Project(db, member, now);
        return true;
    }

    /// <summary>
    /// Gives <paramref name="subjectId"/> on <paramref name="tenantId"/> the reason a team's access to the
    /// tenant (<paramref name="teamAccessId"/>) gives, adding the membership when they hold none, and
    /// stages its joined row naming the team, attributed to <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// The source is explicit, never the scope's actor: the team side acts for the team, and
    /// <see cref="MemberAccessAudit.StageRemoved"/> reads a removal whose actor is the member as the
    /// member leaving the site. Takes the member's lock, after the team access lock the caller holds
    /// (<see cref="MembershipLock"/>).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The access is not of <paramref name="tenantId"/>, the member already holds its reason, or
    /// <paramref name="grant"/> gives the owner role, which no team reason may.
    /// </exception>
    public static async Task<TenantMemberEntity> AddTeamReasonAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, Guid teamAccessId, MembershipGrant grant,
        MemberAccessSource source, DateTime now, CancellationToken ct)
    {
        var access = await db.TeamTenantAccess.AsNoTracking().Include(a => a.Team)
            .FirstOrDefaultAsync(a => a.Id == teamAccessId && a.TenantId == tenantId, ct)
            ?? throw new InvalidOperationException($"Team access {teamAccessId} is not of tenant {tenantId}.");
        await RefuseOwnerRoleAsync(db, grant, ct);

        await db.LockMembershipAsync(tenantId, subjectId, ct);
        var team = new MemberAccessTeam(access.TeamId, access.Team!.Name);
        var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
        if (member is null)
        {
            member = new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId };
            MemberAccessAudit.StageJoined(db, tenantId, subjectId, MembershipJoinHow.Team, inviteId: null, by: null, team, source);
            db.TenantMembers.Add(member);
            var reason = Described(db, NewReason(db, member.Id, MembershipReasonKind.Team, access.Id, grant, createdFromInviteId: null));
            Write(db, member, new MembershipProjection(reason.Grant, NextReasonLapseAt: null), [reason]);
            return member;
        }

        if ((await AlignedAsync(db, member, now, ct)).Any(r => r.SourceId == access.Id))
            throw new InvalidOperationException($"Member {member.Id} already holds the reason of team access {access.Id}.");

        MemberAccessAudit.StageJoined(db, tenantId, subjectId, MembershipJoinHow.Team, inviteId: null, by: null, team, source);
        NewReason(db, member.Id, MembershipReasonKind.Team, access.Id, grant, createdFromInviteId: null);
        Project(db, member, now);
        return member;
    }

    /// <summary>
    /// What the reason a team's access (<paramref name="teamAccessId"/>) gives the member; null when they
    /// hold none from it. Takes the member's lock.
    /// </summary>
    public static async Task<MembershipGrant?> TeamGrantAsync(
        NocturneDbContext db, TenantMemberEntity member, Guid teamAccessId, DateTime now, CancellationToken ct) =>
        (await AlignedAsync(db, member, now, ct)).FirstOrDefault(r => IsOfTeamAccess(r, teamAccessId)) is { } reason
            ? Described(db, reason).Grant
            : null;

    /// <summary>
    /// Removes the reason a team's access (<paramref name="teamAccessId"/>) gives the member and stages
    /// its removed row naming the team, attributed to <paramref name="source"/> as
    /// <see cref="AddTeamReasonAsync"/> explains. A member whose only reason it is is removed with it;
    /// any other keeps its row, projected from what is left.
    /// </summary>
    /// <returns>The end the kept row's was lowered to (<see cref="Write"/>); null when the member was removed.</returns>
    /// <exception cref="InvalidOperationException">The member holds no reason from the access.</exception>
    public static async Task<DateTime?> RemoveTeamReasonAsync(
        NocturneDbContext db, TenantMemberEntity member, Guid teamAccessId, MemberAccessSource source, DateTime now,
        CancellationToken ct)
    {
        var reasons = await AlignedAsync(db, member, now, ct);
        var reason = reasons.FirstOrDefault(r => IsOfTeamAccess(r, teamAccessId))
            ?? throw new InvalidOperationException($"Member {member.Id} has no reason from team access {teamAccessId} to remove.");

        var access = await db.TeamTenantAccess.AsNoTracking().Include(a => a.Team).FirstAsync(a => a.Id == teamAccessId, ct);
        MemberAccessAudit.StageRemoved(
            db, member.TenantId, member.SubjectId, MembershipEndHow.Team, new MemberAccessTeam(access.TeamId, access.Team!.Name), source);
        if (reasons.Count == 1)
        {
            RemoveReasonsAndRow(db, member);
            return null;
        }

        RemoveReason(db, reason);
        return Project(db, member, now);
    }

    /// <summary>
    /// Sets the reason a team's access (<paramref name="teamAccessId"/>) gives the member to
    /// <paramref name="grant"/>. Takes the member's lock.
    /// </summary>
    /// <returns>The end the row's was lowered to (<see cref="Write"/>), or null.</returns>
    /// <exception cref="InvalidOperationException">
    /// The member holds no reason from the access, or <paramref name="grant"/> gives the owner role.
    /// </exception>
    public static async Task<DateTime?> SetTeamReasonAsync(
        NocturneDbContext db, TenantMemberEntity member, Guid teamAccessId, MembershipGrant grant, DateTime now,
        CancellationToken ct)
    {
        await RefuseOwnerRoleAsync(db, grant, ct);
        var reason = (await AlignedAsync(db, member, now, ct)).FirstOrDefault(r => IsOfTeamAccess(r, teamAccessId))
            ?? throw new InvalidOperationException($"Member {member.Id} has no reason from team access {teamAccessId} to set.");

        var held = RolesOf(db, reason.Id);
        db.TenantMemberReasonRoles.RemoveRange(held.Where(rr => !grant.RoleIds.Contains(rr.TenantRoleId)));
        db.TenantMemberReasonRoles.AddRange(grant.RoleIds
            .Distinct()
            .Where(roleId => held.All(rr => rr.TenantRoleId != roleId))
            .Select(roleId => NewReasonRole(reason.Id, roleId)));
        reason.DirectPermissions = grant.DirectPermissions?.ToList();
        reason.LimitTo24Hours = grant.LimitTo24Hours;
        reason.AccessEndsAt = grant.AccessEndsAt;
        return Project(db, member, now);
    }

    /// <summary>The owner role is only ever held directly, so the last-owner rule reads one kind of reason.</summary>
    private static async Task RefuseOwnerRoleAsync(NocturneDbContext db, MembershipGrant grant, CancellationToken ct)
    {
        var roleIds = grant.RoleIds.ToList();
        if (await db.TenantRoles.AnyAsync(r => roleIds.Contains(r.Id) && r.Slug == RoleSeeds.Owner, ct))
            throw new InvalidOperationException("A team reason cannot give the owner role.");
    }

    private static bool IsOfTeamAccess(TenantMemberReasonEntity reason, Guid teamAccessId) =>
        reason.Kind == MembershipReasonKind.Team && reason.SourceId == teamAccessId;

    /// <summary>Sets the direct permissions the member's direct reason gives.</summary>
    public static async Task SetDirectPermissionsAsync(
        NocturneDbContext db, TenantMemberEntity member, IReadOnlyList<string>? permissions, DateTime now, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, now, ct);
        direct.DirectPermissions = permissions?.ToList();
        Project(db, member, now);
    }

    /// <summary>Sets whether the member's direct reason limits them to the last 24 hours.</summary>
    public static async Task SetLimitTo24HoursAsync(
        NocturneDbContext db, TenantMemberEntity member, bool limitTo24Hours, DateTime now, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, now, ct);
        direct.LimitTo24Hours = limitTo24Hours;
        Project(db, member, now);
    }

    /// <summary>Sets when the member's direct reason ends.</summary>
    public static async Task SetAccessEndAsync(
        NocturneDbContext db, TenantMemberEntity member, DateTime? accessEndsAt, DateTime now, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, now, ct);
        direct.AccessEndsAt = accessEndsAt;
        Project(db, member, now);
    }

    /// <summary>
    /// Takes <paramref name="removedRoleIds"/> from the member's direct reason, then gives it
    /// <paramref name="addedRoleIds"/>.
    /// </summary>
    public static async Task ReplaceRolesAsync(
        NocturneDbContext db, TenantMemberEntity member, IReadOnlyCollection<Guid> removedRoleIds,
        IReadOnlyCollection<Guid> addedRoleIds, DateTime now, CancellationToken ct)
    {
        var direct = await DirectReasonAsync(db, member, now, ct);
        db.TenantMemberReasonRoles.RemoveRange(RolesOf(db, direct.Id).Where(rr => removedRoleIds.Contains(rr.TenantRoleId)));
        foreach (var roleId in addedRoleIds)
            db.TenantMemberReasonRoles.Add(NewReasonRole(direct.Id, roleId));
        Project(db, member, now);
    }

    /// <summary>
    /// Removes the member with its reasons and its role links, and stages its removed row attributed to
    /// the scope's actor.
    /// </summary>
    public static async Task RemoveMemberAsync(
        NocturneDbContext db, TenantMemberEntity member, MembershipEndHow how, CancellationToken ct)
    {
        MemberAccessAudit.StageRemoved(db, member.TenantId, member.SubjectId, how, team: null, source: null);
        await LockAndLoadAsync(db, member, ct);
        RemoveReasonsAndRow(db, member);
    }

    private static void RemoveReasonsAndRow(NocturneDbContext db, TenantMemberEntity member)
    {
        foreach (var reason in ReasonsOf(db, member.Id))
            RemoveReason(db, reason);

        db.TenantMemberRoles.RemoveRange(LinksOf(db, member.Id));
        db.TenantMembers.Remove(member);
    }

    private static void RemoveReason(NocturneDbContext db, TenantMemberReasonEntity reason)
    {
        db.TenantMemberReasonRoles.RemoveRange(RolesOf(db, reason.Id));
        db.TenantMemberReasons.Remove(reason);
    }

    /// <summary>
    /// Takes <paramref name="roleId"/> from every reason that gives it and from every member holding
    /// it, before the role itself is removed.
    /// </summary>
    public static async Task RemoveRoleAsync(NocturneDbContext db, Guid roleId, DateTime now, CancellationToken ct)
    {
        var memberIds = await db.TenantMemberRoles
            .Where(mr => mr.TenantRoleId == roleId)
            .Select(mr => mr.TenantMemberId)
            .Union(db.TenantMemberReasonRoles
                .Where(rr => rr.TenantRoleId == roleId)
                .Select(rr => rr.TenantMemberReason.TenantMemberId))
            .ToListAsync(ct);

        var members = await db.TenantMembers.Where(m => memberIds.Contains(m.Id)).ToListAsync(ct);
        await db.LockMembershipsAsync(members.Select(m => (m.TenantId, m.SubjectId)), ct);
        foreach (var member in members)
        {
            foreach (var reason in await AlignedAsync(db, member, now, ct))
                db.TenantMemberReasonRoles.RemoveRange(RolesOf(db, reason.Id).Where(rr => rr.TenantRoleId == roleId));
            Project(db, member, now);
        }
    }

    /// <summary>
    /// Whether <paramref name="row"/>, holding <paramref name="rowRoleIds"/>, is the sum of
    /// <paramref name="reasons"/> in force at <paramref name="now"/> as the remarks define it; never
    /// when it has no reason.
    /// </summary>
    internal static bool Matches(
        TenantMemberEntity row, IReadOnlyCollection<Guid> rowRoleIds,
        IReadOnlyList<(MembershipGrant Grant, string Kind, Guid? InviteId)> reasons, DateTime now)
    {
        if (MembershipSum.InForceAt(reasons.Select(r => r.Grant).ToList(), now) is not { } projection)
            return false;

        var sum = projection.Grant;
        return rowRoleIds.ToHashSet().SetEquals(sum.RoleIds)
            && SamePermissions(row.DirectPermissions, sum.DirectPermissions)
            && row.LimitTo24Hours == sum.LimitTo24Hours
            && row.AccessEndsAt == sum.AccessEndsAt
            && row.NextReasonLapseAt == projection.NextReasonLapseAt
            && row.CreatedFromInviteId == DirectInvite(reasons);
    }

    /// <summary>Whether the member's row is the sum of its reasons at <paramref name="now"/>, by <see cref="Matches"/>.</summary>
    internal static async Task<bool> MatchesAsync(NocturneDbContext db, Guid memberId, DateTime now, CancellationToken ct)
    {
        var member = await db.TenantMembers.FirstAsync(m => m.Id == memberId, ct);
        await LoadAsync(db, memberId, ct);
        return Matches(
            member,
            LinksOf(db, memberId).Select(mr => mr.TenantRoleId).ToList(),
            ReasonsOf(db, memberId).Select(r => Described(db, r)).ToList(),
            now);
    }

    /// <summary>
    /// <see cref="RecomputeAsync(NocturneDbContext, Guid, Guid, DateTime, CancellationToken)"/> on a
    /// context of its own, pinned to <paramref name="tenantId"/>.
    /// </summary>
    internal static async Task RecomputeAsync(
        INocturneDbContextFactory contexts, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        await RecomputeAsync(db, tenantId, subjectId, now, ct);
    }

    /// <summary>
    /// Projects the membership of <paramref name="subjectId"/> on <paramref name="tenantId"/> again
    /// from its reasons in force at <paramref name="now"/>, in a transaction on <paramref name="db"/>
    /// under the member's lock, when the row read under the lock is due; otherwise writes nothing.
    /// </summary>
    /// <remarks>
    /// No one acted, so it stages no audit row. A due row's next lapse always changes, so the save
    /// moves its <see cref="TenantMemberEntity.SysUpdatedAt"/>, as every projection that changes a row does.
    /// A lapse never moves the end of a row aligned with its reasons (<see cref="MembershipSum"/>), so
    /// it caps nothing the member made.
    /// </remarks>
    internal static Task RecomputeAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
        db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockMembershipAsync(tenantId, subjectId, token);
            var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, token);
            if (member is null || !MembershipLiveness.IsDue(member.NextReasonLapseAt, now))
                return;

            await LoadAsync(db, member.Id, token);
            Project(db, member, now);
            await db.SaveChangesAsync(token);
        }, ct);

    /// <summary>
    /// <see cref="ReadAllInForceAsync"/> for a read of one membership, which returns its value with the
    /// subject and next lapse of the row it found (a null lapse when it found none).
    /// </summary>
    internal static Task<T> ReadInForceAsync<T>(
        INocturneDbContextFactory? contexts, Guid tenantId, DateTime now,
        Func<Expression<Func<TenantMemberEntity, bool>>, CancellationToken, Task<(T Value, Guid SubjectId, DateTime? NextReasonLapseAt)>> read,
        CancellationToken ct) =>
        ReadAllInForceAsync(contexts, tenantId, now, async (inForce, token) =>
        {
            var (value, subjectId, lapse) = await read(inForce, token);
            return (value, (IReadOnlyList<(Guid, DateTime?)>)[(subjectId, lapse)]);
        }, ct);

    /// <summary>
    /// Reads memberships of <paramref name="tenantId"/> through <paramref name="read"/>, which is given
    /// the predicate the rows must meet and returns its value with the subject and next lapse of each
    /// row it found. It first selects by end alone (<see cref="MembershipLiveness.IsUnended"/>); when a
    /// row it finds is due, it recomputes each due row and reads once more, selecting only live rows
    /// (<see cref="MembershipLiveness.IsLive"/>).
    /// </summary>
    /// <remarks>
    /// For the reads that decide a request or a sign-in, so a lapse narrows a member at once rather
    /// than shutting them out until the sweep. With no <paramref name="contexts"/> (a repository over
    /// a caller's unit of work) nothing is recomputed, and a due row reads as absent. A recompute that
    /// fails (<see cref="IsMembershipFailure"/>) is logged and its row also reads as absent, so the
    /// caller is refused as a non-member rather than failed.
    /// </remarks>
    internal static async Task<T> ReadAllInForceAsync<T>(
        INocturneDbContextFactory? contexts, Guid tenantId, DateTime now,
        Func<Expression<Func<TenantMemberEntity, bool>>, CancellationToken, Task<(T Value, IReadOnlyList<(Guid SubjectId, DateTime? NextReasonLapseAt)> Rows)>> read,
        CancellationToken ct)
    {
        var first = await read(MembershipLiveness.IsUnended(now), ct);
        var due = first.Rows
            .Where(r => MembershipLiveness.IsDue(r.NextReasonLapseAt, now))
            .Select(r => r.SubjectId)
            .Distinct()
            .ToList();
        if (due.Count == 0)
            return first.Value;

        if (contexts is not null)
        {
            foreach (var subjectId in due)
                await RecomputeOrLeaveDueAsync(contexts, tenantId, subjectId, now, ct);
        }

        return (await read(MembershipLiveness.IsLive(now), ct)).Value;
    }

    private static async Task RecomputeOrLeaveDueAsync(
        INocturneDbContextFactory contexts, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        try
        {
            await RecomputeAsync(db, tenantId, subjectId, now, ct);
        }
        catch (Exception ex) when (IsMembershipFailure(ex))
        {
            db.GetService<ILoggerFactory>().CreateLogger(typeof(MembershipReasons)).LogError(
                ex, "Could not recompute the membership of subject {SubjectId} on tenant {TenantId} after a reason lapsed; it reads as absent.",
                subjectId, tenantId);
        }
    }

    /// <summary>
    /// Recomputes every membership due at <paramref name="now"/>, each in its own transaction on a
    /// context <paramref name="openForTenant"/> opens; one that fails (<see cref="IsMembershipFailure"/>)
    /// is logged and the rest still run.
    /// </summary>
    /// <param name="openPlatform">Opens the context that finds the due rows of every tenant.</param>
    /// <param name="openForTenant">Opens a context pinned to a tenant.</param>
    /// <param name="now">The instant the rows are due at and projected for.</param>
    /// <param name="logger">Logs each membership that could not be recomputed.</param>
    /// <param name="ct">A cancellation token.</param>
    internal static async Task<MembershipLapseSweep> RecomputeDueAsync(
        Func<CancellationToken, ValueTask<NocturneDbContext>> openPlatform,
        Func<Guid, CancellationToken, ValueTask<NocturneDbContext>> openForTenant,
        DateTime now, ILogger logger, CancellationToken ct)
    {
        List<(Guid TenantId, Guid SubjectId)> due;
        await using (var db = await openPlatform(ct))
        {
            due = (await db.TenantMembers.AsNoTracking()
                    .Where(m => m.NextReasonLapseAt <= now)
                    .OrderBy(m => m.TenantId)
                    .ThenBy(m => m.SubjectId)
                    .Select(m => new { m.TenantId, m.SubjectId })
                    .ToListAsync(ct))
                .Select(m => (m.TenantId, m.SubjectId))
                .ToList();
        }

        var failed = await EachMembershipAsync(
            due,
            async membership =>
            {
                await using var db = await openForTenant(membership.TenantId, ct);
                await RecomputeAsync(db, membership.TenantId, membership.SubjectId, now, ct);
            },
            (membership, ex) => logger.LogError(
                ex, "Could not recompute the membership of subject {SubjectId} on tenant {TenantId} after a reason lapsed.",
                membership.SubjectId, membership.TenantId));

        return new MembershipLapseSweep(due.Count - failed, failed);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on each of <paramref name="memberships"/> in turn. One that fails
    /// (<see cref="IsMembershipFailure"/>) is handed to <paramref name="logFailure"/>, and the rest still run.
    /// </summary>
    /// <returns>How many failed.</returns>
    internal static async Task<int> EachMembershipAsync<T>(
        IEnumerable<T> memberships, Func<T, Task> work, Action<T, Exception> logFailure)
    {
        var failed = 0;
        foreach (var membership in memberships)
        {
            try
            {
                await work(membership);
            }
            catch (Exception ex) when (IsMembershipFailure(ex))
            {
                failed++;
                logFailure(membership, ex);
            }
        }

        return failed;
    }

    /// <summary>
    /// What reading or writing one membership can meet that need not stop the others: the database or
    /// the save failing, and a malformed row (a <c>jsonb</c> value that is not a list of strings fails
    /// to read as a <see cref="JsonException"/>, a cast or a format error).
    /// </summary>
    internal static bool IsMembershipFailure(Exception ex) =>
        ex is DbException or DbUpdateException or InvalidOperationException or TimeoutException
            or JsonException or InvalidCastException or FormatException or NotSupportedException or ArgumentException;

    /// <summary>
    /// Brings every member row and its reasons together by <see cref="AlignWithRow"/> at
    /// <paramref name="now"/>, for the startup check: one read finds the members that do not match,
    /// then each is aligned from what it reads in its own transaction, on a context
    /// <paramref name="open"/> opens, under its lock. A member whose end the alignment lowers has what
    /// they made capped at that end (<see cref="MemberCreations.CapAsync"/>) in the same transaction.
    /// One that fails (<see cref="IsMembershipFailure"/>) is logged and the rest still run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One transaction per member holds one lock at a time, however many members there are.
    /// </para>
    /// <para>
    /// A row another build wrote can hold an end its reasons do not give, so aligning it can lower the
    /// end. Once it is aligned, a lapse cannot (<see cref="MembershipSum"/>), so
    /// <see cref="RecomputeAsync(NocturneDbContext, Guid, Guid, DateTime, CancellationToken)"/> caps
    /// nothing. The check runs before the app serves, so no session is cached to evict.
    /// </para>
    /// </remarks>
    internal static async Task<MembershipReconciliation> ReconcileAsync(
        Func<NocturneDbContext> open, DateTime now, ILogger logger, CancellationToken ct)
    {
        List<TenantMemberEntity> apart;
        await using (var db = open())
        {
            var members = await db.TenantMembers.AsNoTracking()
                .OrderBy(m => m.TenantId)
                .ThenBy(m => m.SubjectId)
                .ToListAsync(ct);
            await db.TenantMemberRoles.LoadAsync(ct);
            await db.TenantMemberReasons.LoadAsync(ct);
            await db.TenantMemberReasonRoles.LoadAsync(ct);
            apart = members.Where(m => AgreementOf(db, m, now) != RowAgreement.Matches).ToList();
        }

        var adopted = 0;
        var resynced = new List<Guid>();
        var reprojected = new List<Guid>();
        var failed = await EachMembershipAsync(
            apart,
            async candidate =>
            {
                await using var unit = DbUnitOfWork.Own(open());
                var db = unit.Context;
                var agreement = await unit.CommitAsync(async token =>
                {
                    await db.LockMembershipAsync(candidate.TenantId, candidate.SubjectId, token);
                    var member = await db.TenantMembers.FirstOrDefaultAsync(m => m.Id == candidate.Id, token);
                    if (member is null)
                        return RowAgreement.Matches;

                    await LoadAsync(db, member.Id, token);
                    var (outcome, loweredTo) = AlignWithRow(db, member, now);
                    if (loweredTo is { } end)
                    {
                        await db.PinTenantAsync(member.TenantId, token);
                        await MemberCreations.CapAsync(unit, member.SubjectId, end, now, token);
                    }
                    if (outcome != RowAgreement.Matches)
                        await db.SaveChangesAsync(token);
                    return outcome;
                }, ct: ct);

                switch (agreement)
                {
                    case RowAgreement.Adopted:
                        adopted++;
                        break;
                    case RowAgreement.Resynced:
                        resynced.Add(candidate.Id);
                        break;
                    case RowAgreement.Reprojected:
                        reprojected.Add(candidate.Id);
                        break;
                }
            },
            (candidate, ex) => logger.LogError(
                ex, "Could not align the membership of subject {SubjectId} on tenant {TenantId} with its reasons; it is checked again on the next startup.",
                candidate.SubjectId, candidate.TenantId));

        return new MembershipReconciliation(adopted, resynced, reprojected, failed);
    }

    /// <summary>
    /// The member's direct reason among <see cref="AlignedAsync"/>. Its callers have read under the
    /// member's lock that it holds one (<see cref="DirectGrantAsync"/>), so a member with no direct
    /// reason never reaches it.
    /// </summary>
    private static async Task<TenantMemberReasonEntity> DirectReasonAsync(
        NocturneDbContext db, TenantMemberEntity member, DateTime now, CancellationToken ct) =>
        (await AlignedAsync(db, member, now, ct)).FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct)
            ?? throw new InvalidOperationException($"Member {member.Id} has reasons but no direct reason to change.");

    /// <summary>
    /// The member's reasons after <see cref="AlignWithRow"/> has brought them into line with the row.
    /// Takes the member's lock and loads its reasons and links onto the context. A re-sync is logged at
    /// warning and writes no audit row: no access changed, only the reason had fallen behind the row.
    /// </summary>
    private static async Task<List<TenantMemberReasonEntity>> AlignedAsync(
        NocturneDbContext db, TenantMemberEntity member, DateTime now, CancellationToken ct)
    {
        await LockAndLoadAsync(db, member, ct);
        if (AlignWithRow(db, member, now).Agreement == RowAgreement.Resynced)
        {
            db.GetService<ILoggerFactory>().CreateLogger(typeof(MembershipReasons)).LogWarning(
                "Re-synced the direct reason of membership {MemberId} from its row, which did not match it.", member.Id);
        }

        return ReasonsOf(db, member.Id);
    }

    private enum RowAgreement
    {
        Matches,
        Adopted,
        Resynced,
        Reprojected,
    }

    /// <summary>What <see cref="AlignWithRow"/> does to the member, from its loaded reasons and links, changing nothing.</summary>
    private static RowAgreement AgreementOf(NocturneDbContext db, TenantMemberEntity member, DateTime now)
    {
        var reasons = ReasonsOf(db, member.Id);
        if (reasons.Count == 0)
            return RowAgreement.Adopted;
        if (Matches(member, LinksOf(db, member.Id).Select(mr => mr.TenantRoleId).ToList(),
                reasons.Select(r => Described(db, r)).ToList(), now))
            return RowAgreement.Matches;
        return reasons is [{ Kind: MembershipReasonKind.Direct }] ? RowAgreement.Resynced : RowAgreement.Reprojected;
    }

    /// <summary>
    /// Brings a member's loaded reasons and row into line: a member with no reason is given its direct
    /// reason from the row; a member whose one reason is direct and does not match it has that reason
    /// re-synced from the row, and nothing on the row changes but a next lapse its one reason cannot
    /// give; any other member that does not match
    /// has its row projected from its reasons in force at <paramref name="now"/>. Returns what it did,
    /// with the end that projection lowered the row's to (<see cref="Write"/>).
    /// </summary>
    /// <remarks>
    /// The row wins over a direct reason because a build that ran after a rollback, or beside this
    /// one, wrote the row and knew nothing of reasons, so taking the reason would bring back access
    /// someone removed. Only this build writes a reason of another kind, and the row of a member who
    /// holds one only through it, so for that member the reasons win.
    /// </remarks>
    private static (RowAgreement Agreement, DateTime? EndLoweredTo) AlignWithRow(
        NocturneDbContext db, TenantMemberEntity member, DateTime now)
    {
        var agreement = AgreementOf(db, member, now);
        switch (agreement)
        {
            case RowAgreement.Matches:
                return (agreement, null);
            case RowAgreement.Adopted:
                NewReason(
                    db, member.Id, MembershipReasonKind.Direct, sourceId: null,
                    new MembershipGrant(
                        LinksOf(db, member.Id).Select(mr => mr.TenantRoleId).ToList(),
                        member.DirectPermissions, member.LimitTo24Hours, member.AccessEndsAt),
                    member.CreatedFromInviteId);
                return (agreement, null);
            case RowAgreement.Reprojected:
                return (agreement, Project(db, member, now));
        }

        var direct = ReasonsOf(db, member.Id).Single();
        var links = LinksOf(db, member.Id).Select(mr => mr.TenantRoleId).ToHashSet();
        var held = RolesOf(db, direct.Id);
        db.TenantMemberReasonRoles.RemoveRange(held.Where(rr => !links.Contains(rr.TenantRoleId)));
        db.TenantMemberReasonRoles.AddRange(links
            .Where(roleId => held.All(rr => rr.TenantRoleId != roleId))
            .Select(roleId => NewReasonRole(direct.Id, roleId)));
        direct.DirectPermissions = member.DirectPermissions?.ToList();
        direct.LimitTo24Hours = member.LimitTo24Hours;
        direct.AccessEndsAt = member.AccessEndsAt;
        direct.CreatedFromInviteId = member.CreatedFromInviteId;
        return (agreement, Project(db, member, now));
    }

    /// <summary>
    /// Writes the row and its role links to match the member's reasons in force at
    /// <paramref name="now"/>, touching nothing that already does, and returns what <see cref="Write"/> does.
    /// </summary>
    private static DateTime? Project(NocturneDbContext db, TenantMemberEntity member, DateTime now)
    {
        var reasons = ReasonsOf(db, member.Id).Select(r => Described(db, r)).ToList();
        var projection = MembershipSum.InForceAt(reasons.Select(r => r.Grant).ToList(), now)
            ?? throw new InvalidOperationException($"Member {member.Id} has no reason to project.");
        return Write(db, member, projection, reasons);
    }

    /// <summary>Writes <paramref name="projection"/> to the row and its role links.</summary>
    /// <returns>
    /// The row's new end when it is earlier than the end the row was read with, or the row was read with
    /// none; otherwise null. It is measured from the read, not from the row as this context last changed
    /// it, so an end that <see cref="AlignWithRow"/> lowered before this write is reported too. What the
    /// member made outlives such an end until its caller caps it (<see cref="MemberCreations"/>).
    /// </returns>
    private static DateTime? Write(
        NocturneDbContext db, TenantMemberEntity member, MembershipProjection projection,
        IReadOnlyList<(MembershipGrant Grant, string Kind, Guid? InviteId)> reasons)
    {
        var endBefore = db.Entry(member).Property(m => m.AccessEndsAt).OriginalValue;
        var sum = projection.Grant;
        var links = LinksOf(db, member.Id);
        db.TenantMemberRoles.RemoveRange(links.Where(mr => !sum.RoleIds.Contains(mr.TenantRoleId)));
        foreach (var roleId in sum.RoleIds.Distinct().Where(id => links.All(mr => mr.TenantRoleId != id)))
        {
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantMemberId = member.Id,
                TenantRoleId = roleId,
            });
        }

        if (!SamePermissions(member.DirectPermissions, sum.DirectPermissions))
            member.DirectPermissions = sum.DirectPermissions?.ToList();
        if (member.LimitTo24Hours != sum.LimitTo24Hours)
            member.LimitTo24Hours = sum.LimitTo24Hours;
        if (member.AccessEndsAt != sum.AccessEndsAt)
            member.AccessEndsAt = sum.AccessEndsAt;
        if (member.NextReasonLapseAt != projection.NextReasonLapseAt)
            member.NextReasonLapseAt = projection.NextReasonLapseAt;
        if (DirectInvite(reasons) is var invite && member.CreatedFromInviteId != invite)
            member.CreatedFromInviteId = invite;

        return sum.AccessEndsAt is { } end && (endBefore is not { } before || end < before) ? end : null;
    }

    /// <summary>
    /// Takes the member's lock, reads its row again unless this context has already changed it, and
    /// loads its reasons and links (<see cref="LoadAsync"/>).
    /// </summary>
    private static async Task LockAndLoadAsync(NocturneDbContext db, TenantMemberEntity member, CancellationToken ct)
    {
        await db.LockMembershipAsync(member.TenantId, member.SubjectId, ct);
        var entry = db.Entry(member);
        if (entry.State == EntityState.Unchanged)
            await entry.ReloadAsync(ct);
        await LoadAsync(db, member.Id, ct);
    }

    /// <summary>
    /// Puts the member's reasons, their roles and the member's role links on the context, beside any
    /// the context already tracks.
    /// </summary>
    private static async Task LoadAsync(NocturneDbContext db, Guid memberId, CancellationToken ct)
    {
        await db.TenantMemberReasons.Where(r => r.TenantMemberId == memberId).LoadAsync(ct);
        await db.TenantMemberReasonRoles.Where(rr => rr.TenantMemberReason.TenantMemberId == memberId).LoadAsync(ct);
        await db.TenantMemberRoles.Where(mr => mr.TenantMemberId == memberId).LoadAsync(ct);
    }

    private static List<TenantMemberReasonEntity> ReasonsOf(NocturneDbContext db, Guid memberId) =>
        db.TenantMemberReasons.Local.Where(r => r.TenantMemberId == memberId).ToList();

    private static List<TenantMemberReasonRoleEntity> RolesOf(NocturneDbContext db, Guid reasonId) =>
        db.TenantMemberReasonRoles.Local.Where(rr => rr.TenantMemberReasonId == reasonId).ToList();

    private static List<TenantMemberRoleEntity> LinksOf(NocturneDbContext db, Guid memberId) =>
        db.TenantMemberRoles.Local.Where(mr => mr.TenantMemberId == memberId).ToList();

    private static (MembershipGrant Grant, string Kind, Guid? InviteId) Described(
        NocturneDbContext db, TenantMemberReasonEntity reason) =>
        (new MembershipGrant(
            RolesOf(db, reason.Id).Select(rr => rr.TenantRoleId).ToList(),
            reason.DirectPermissions,
            reason.LimitTo24Hours,
            reason.AccessEndsAt), reason.Kind, reason.CreatedFromInviteId);

    private static Guid? DirectInvite(IEnumerable<(MembershipGrant Grant, string Kind, Guid? InviteId)> reasons) =>
        reasons.FirstOrDefault(r => r.Kind == MembershipReasonKind.Direct).InviteId;

    private static bool SamePermissions(IReadOnlyList<string>? row, IReadOnlyList<string>? sum) =>
        row is null ? sum is null : sum is not null && row.SequenceEqual(sum, StringComparer.Ordinal);

    private static TenantMemberReasonEntity NewReason(
        NocturneDbContext db, Guid memberId, string kind, Guid? sourceId, MembershipGrant grant, Guid? createdFromInviteId)
    {
        var reason = new TenantMemberReasonEntity
        {
            Id = Guid.CreateVersion7(),
            TenantMemberId = memberId,
            Kind = kind,
            SourceId = sourceId,
            DirectPermissions = grant.DirectPermissions?.ToList(),
            LimitTo24Hours = grant.LimitTo24Hours,
            AccessEndsAt = grant.AccessEndsAt,
            CreatedFromInviteId = createdFromInviteId,
        };
        reason.ReasonRoles = grant.RoleIds.Distinct().Select(roleId => NewReasonRole(reason.Id, roleId)).ToList();
        db.TenantMemberReasons.Add(reason);
        return reason;
    }

    private static TenantMemberReasonRoleEntity NewReasonRole(Guid reasonId, Guid roleId) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantMemberReasonId = reasonId,
        TenantRoleId = roleId,
    };
}

/// <summary>What <see cref="MembershipReasons.ReconcileAsync"/> did.</summary>
/// <param name="Adopted">Members that had no reason and were given their direct reason.</param>
/// <param name="Resynced">Members whose direct reason was re-synced from the row.</param>
/// <param name="Reprojected">Members that hold a reason of another kind, whose row was projected from their reasons.</param>
/// <param name="Failed">Members that could not be aligned, each logged; the next startup checks them again.</param>
internal sealed record MembershipReconciliation(
    int Adopted, IReadOnlyList<Guid> Resynced, IReadOnlyList<Guid> Reprojected, int Failed);
