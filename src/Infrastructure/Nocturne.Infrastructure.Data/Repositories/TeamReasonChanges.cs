using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// The team reasons a team write adds, sets and removes, read under its locks, and the write those
/// reasons stand on. For each site in <see cref="Sites"/>, with the unit pinned to it, the caller stops
/// what each member in <see cref="EndingAt"/> made there, then calls <see cref="StageReasonsAtAsync"/>,
/// then saves. Last it calls <see cref="StageAsync"/> and saves.
/// </summary>
/// <remarks>
/// <para>
/// A write that removes what a reason stands on (<see cref="TeamRepository.LockAccessStopAsync"/> and its
/// siblings) is the write staged last: a reason's source cannot be deleted while the reason names it,
/// so every reason goes, and is saved, first. A write that changes what the team's rows give
/// (<see cref="TeamRepository.LockStaffJoinAsync"/> and its siblings) has saved itself already, and the
/// changes are what makes the subjects' team reasons agree with the rows again (<see cref="SyncAsync"/>).
/// </para>
/// <para>
/// Every change is attributed to the system: the site's records do not show the team's internals.
/// </para>
/// </remarks>
public sealed class TeamReasonChanges
{
    private readonly DbUnitOfWork _unit;
    private readonly IReadOnlyList<Removal> _removals;
    private readonly IReadOnlyList<Given> _given;
    private readonly Func<NocturneDbContext, CancellationToken, Task> _write;

    /// <summary>What the owners of the sites are owed once the write commits: <see cref="WithStaffNotices"/>.</summary>
    public StaffNotices Notices { get; }

    /// <summary>A reason removed; <c>Last</c> when the membership ends with the removals, holding no other reason and gaining none.</summary>
    private sealed record Removal(Guid TenantId, Guid SubjectId, Guid AccessId, bool Last);

    /// <summary>A reason added, or set when <c>Held</c> says the subject holds it already.</summary>
    private sealed record Given(Guid TenantId, Guid SubjectId, Guid AccessId, MembershipGrant Grant, bool Held);

    private TeamReasonChanges(
        DbUnitOfWork unit, IReadOnlyList<Removal> removals, IReadOnlyList<Given> given,
        Func<NocturneDbContext, CancellationToken, Task> write, StaffNotices notices)
    {
        _unit = unit;
        _removals = removals;
        _given = given;
        _write = write;
        Notices = notices;
    }

    /// <summary>
    /// These changes, owing the owners of the sites once they commit a recount of <paramref name="teamId"/>'s
    /// waiting staff at <paramref name="recountSites"/>, and a notice that the team's staff newly reach each
    /// site where a reason is added. An owner is given no reason, so no owner is announced as joined.
    /// </summary>
    public TeamReasonChanges WithStaffNotices(Guid teamId, IEnumerable<Guid> recountSites) => new(
        _unit, _removals, _given, _write,
        new StaffNotices(
            recountSites.Distinct().Order().Select(site => new TeamSiteKey(site, teamId)).ToList(),
            _given.Where(g => !g.Held).Select(g => g.TenantId).Distinct().Order().Select(site => new TeamSiteKey(site, teamId)).ToList()));

    /// <summary>The sites where a team reason changes, in ascending tenant id.</summary>
    public IReadOnlyList<Guid> Sites =>
        _removals.Select(r => r.TenantId).Concat(_given.Select(g => g.TenantId)).Distinct().Order().ToList();

    /// <summary>The subjects whose membership of <paramref name="tenantId"/> ends with these changes.</summary>
    public IReadOnlyList<Guid> EndingAt(Guid tenantId) =>
        _removals.Where(r => r.TenantId == tenantId && r.Last).Select(r => r.SubjectId).Distinct().ToList();

    /// <summary>
    /// Stages every change at <paramref name="tenantId"/>: each reason added
    /// (<see cref="TenantMembershipRepository.StageAddTeamReasonAsync"/>) and set
    /// (<see cref="TenantMembershipRepository.StageSetTeamReasonAsync"/>), then each removed
    /// (<see cref="TenantMembershipRepository.StageRemoveTeamReasonAsync"/>), so a member who gains one
    /// reason as they lose another keeps the membership. What a member who keeps their membership made is
    /// capped at an end the changes lower. The unit must be pinned to the tenant.
    /// </summary>
    /// <returns>The grants the caps lowered, for the caller to evict their sessions once the unit commits.</returns>
    public async Task<IReadOnlyList<OAuthGrantRecord>> StageReasonsAtAsync(Guid tenantId, DateTime now, CancellationToken ct)
    {
        var capped = new List<OAuthGrantRecord>();
        foreach (var given in _given.Where(g => g.TenantId == tenantId))
        {
            if (given.Held)
            {
                capped.AddRange(await TenantMembershipRepository.StageSetTeamReasonAsync(
                    _unit, given.TenantId, given.SubjectId, given.AccessId, given.Grant, MemberAccessSource.System, now, ct));
            }
            else
            {
                await TenantMembershipRepository.StageAddTeamReasonAsync(
                    _unit.Context, given.TenantId, given.SubjectId, given.AccessId, given.Grant, MemberAccessSource.System, now, ct);
            }
        }

        foreach (var removal in _removals.Where(r => r.TenantId == tenantId))
        {
            capped.AddRange(await TenantMembershipRepository.StageRemoveTeamReasonAsync(
                _unit, removal.TenantId, removal.SubjectId, removal.AccessId, MemberAccessSource.System, now, ct) ?? []);
        }

        return capped;
    }

    /// <summary>Stages the write the reasons stood on. Saves nothing.</summary>
    public Task StageAsync(CancellationToken ct) => _write(_unit.Context, ct);

    /// <summary>
    /// Reads the team reasons that stand on <paramref name="accessIds"/>, only <paramref name="subjectId"/>'s
    /// when it is set, takes their memberships' locks, and reads them again under the locks, which only
    /// a removal can have changed: the caller holds the access locks every reason's writer takes. Each is
    /// removed, and <paramref name="write"/> is staged after them.
    /// </summary>
    internal static async Task<TeamReasonChanges> ReadAsync(
        DbUnitOfWork unit, IReadOnlyCollection<Guid> accessIds, Guid? subjectId,
        Func<NocturneDbContext, CancellationToken, Task> write, CancellationToken ct)
    {
        var db = unit.Context;
        var standing = await StandingOn(db, accessIds, subjectId)
            .Select(r => new { r.TenantMember.TenantId, r.TenantMember.SubjectId })
            .ToListAsync(ct);
        await db.LockMembershipsAsync(standing.Select(m => (m.TenantId, m.SubjectId)), ct);

        var removals = await StandingOn(db, accessIds, subjectId)
            .OrderBy(r => r.TenantMember.TenantId)
            .ThenBy(r => r.TenantMember.SubjectId)
            .Select(r => new Removal(
                r.TenantMember.TenantId,
                r.TenantMember.SubjectId,
                r.SourceId!.Value,
                !r.TenantMember.Reasons.Any(other => other.Id != r.Id)))
            .ToListAsync(ct);
        return new TeamReasonChanges(unit, removals, [], write, StaffNotices.None);
    }

    /// <summary>
    /// Takes the locks of <paramref name="memberships"/> and reads, under them, what makes each one's team
    /// reasons agree with the team's rows (<see cref="TeamReasons.DesiredAsync"/>): a reason added for each
    /// access that gives one and has not ended, a reason set where what it gives differs, and a reason
    /// removed where its access gives none. A reason whose end has passed is left as it is while what its
    /// access gives has ended too: the lapse takes it out of the sum, and it goes with its source. One
    /// whose access now gives a later end is set, and is in force again. Writes nothing.
    /// </summary>
    /// <remarks>
    /// The caller holds, in the order <see cref="MembershipLock"/> states, the row of every subject named
    /// (<see cref="TenantOwnerLock.LockSubjectRowsSharedAsync"/>), since adding a reason can add the
    /// membership, and the team access lock of every site named, which every writer of the rows read here
    /// takes, so they hold still to the commit. Whether the subject owns the site is read under the
    /// membership's lock, which every writer of a direct reason holds.
    /// </remarks>
    public static async Task<TeamReasonChanges> SyncAsync(
        DbUnitOfWork unit, IEnumerable<(Guid TenantId, Guid SubjectId)> memberships, DateTime now, CancellationToken ct)
    {
        var db = unit.Context;
        var pairs = memberships.Distinct().OrderBy(m => m.TenantId).ThenBy(m => m.SubjectId).ToList();
        await db.LockMembershipsAsync(pairs, ct);

        var removals = new List<Removal>();
        var given = new List<Given>();
        foreach (var (tenantId, subjectId) in pairs)
        {
            var desired = await TeamReasons.DesiredAsync(db, tenantId, subjectId, now, ct);
            var member = await db.TenantMembers.AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId)
                .Select(m => new
                {
                    Reasons = m.Reasons.Count,
                    Team = m.Reasons
                        .Where(r => r.Kind == MembershipReasonKind.Team && r.SourceId != null)
                        .Select(r => new
                        {
                            AccessId = r.SourceId!.Value,
                            RoleIds = r.ReasonRoles.Select(rr => rr.TenantRoleId).ToList(),
                            r.DirectPermissions,
                            r.LimitTo24Hours,
                            r.AccessEndsAt,
                        })
                        .ToList(),
                })
                .FirstOrDefaultAsync(ct);
            var held = (member?.Team ?? []).ToDictionary(
                r => r.AccessId,
                r => new MembershipGrant(r.RoleIds, r.DirectPermissions, r.LimitTo24Hours, r.AccessEndsAt));

            var gone = held.Keys.Where(accessId => !desired.ContainsKey(accessId)).Order().ToList();
            var adds = 0;
            foreach (var (accessId, grant) in desired.OrderBy(d => d.Key))
            {
                if (held.TryGetValue(accessId, out var current))
                {
                    if (!TeamReasons.Same(current, grant)
                        && !(MembershipLiveness.HasEnded(current.AccessEndsAt, now) && MembershipLiveness.HasEnded(grant.AccessEndsAt, now)))
                        given.Add(new Given(tenantId, subjectId, accessId, grant, Held: true));
                }
                else if (!MembershipLiveness.HasEnded(grant.AccessEndsAt, now))
                {
                    given.Add(new Given(tenantId, subjectId, accessId, grant, Held: false));
                    adds++;
                }
            }

            var last = adds == 0 && gone.Count > 0 && gone.Count == member!.Reasons;
            removals.AddRange(gone.Select(accessId => new Removal(tenantId, subjectId, accessId, last)));
        }

        return new TeamReasonChanges(unit, removals, given, (_, _) => Task.CompletedTask, StaffNotices.None);
    }

    /// <summary>
    /// Brings the team reasons of every membership a team's rows reach into line with them, for the
    /// startup check: one read finds each site and subject with an approved <c>team_site_staff</c> row or a
    /// team reason, then each is synced (<see cref="SyncAsync"/>) in its own transaction, on a context
    /// <paramref name="open"/> opens, under the subject's row and the site's team access lock, and what
    /// changes is handed to <paramref name="apply"/>, which stages and saves it. One that fails
    /// (<see cref="MembershipReasons.IsMembershipFailure"/>) is logged and the rest still run.
    /// </summary>
    /// <remarks>
    /// An instance with no team runs the one read and nothing else.
    /// </remarks>
    /// <returns>How many memberships changed, and how many failed.</returns>
    internal static async Task<(int Synced, int Failed)> SyncAllAsync(
        Func<NocturneDbContext> open, Func<DbUnitOfWork, TeamReasonChanges, CancellationToken, Task> apply, DateTime now,
        ILogger logger, CancellationToken ct)
    {
        List<(Guid TenantId, Guid SubjectId)> reached;
        await using (var db = open())
        {
            reached = (await db.TeamSiteStaff.AsNoTracking()
                    .Where(s => s.Status == TeamSiteStaffStatus.Approved && s.TeamMember!.Kind == TeamMemberKind.Staff)
                    .Select(s => new { s.TeamTenantAccess!.TenantId, s.TeamMember!.SubjectId })
                    .Union(db.TenantMemberReasons.AsNoTracking()
                        .Where(r => r.Kind == MembershipReasonKind.Team)
                        .Select(r => new { r.TenantMember.TenantId, r.TenantMember.SubjectId }))
                    .ToListAsync(ct))
                .Select(m => (m.TenantId, m.SubjectId))
                .OrderBy(m => m.TenantId)
                .ThenBy(m => m.SubjectId)
                .ToList();
        }

        var synced = 0;
        var failed = await MembershipReasons.EachMembershipAsync(
            reached,
            async membership =>
            {
                await using var unit = DbUnitOfWork.Own(open());
                var db = unit.Context;
                var changed = await unit.CommitAsync(async token =>
                {
                    await db.LockSubjectRowSharedAsync(membership.SubjectId, token);
                    await db.LockTeamAccessAsync(membership.TenantId, token);
                    var changes = await SyncAsync(unit, [membership], now, token);
                    if (changes.Sites.Count == 0)
                        return false;

                    await apply(unit, changes, token);
                    return true;
                }, ct: ct);
                if (changed)
                    synced++;
            },
            (membership, ex) => logger.LogError(
                ex, "Could not bring the team reasons of subject {SubjectId} on tenant {TenantId} into line with the team's rows; they are checked again on the next startup.",
                membership.SubjectId, membership.TenantId));

        return (synced, failed);
    }

    private static IQueryable<TenantMemberReasonEntity> StandingOn(
        NocturneDbContext db, IReadOnlyCollection<Guid> accessIds, Guid? subjectId) =>
        db.TenantMemberReasons.AsNoTracking()
            .Where(r => r.Kind == MembershipReasonKind.Team
                && r.SourceId != null
                && accessIds.Contains(r.SourceId.Value)
                && (subjectId == null || r.TenantMember.SubjectId == subjectId));
}
