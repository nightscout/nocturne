using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// A team's access to sites: the links a manager makes, the access an owner of a site approves, and
/// the site's own list and stop. The team side reads as the platform; the site side reads pinned to
/// the site, whose <c>tenant_pin</c> policy holds it to that site's rows even if its query did not.
/// </summary>
public partial class TeamRepository
{
    /// <inheritdoc/>
    public virtual Task<Guid?> CreateAccessLinkAsync(
        Guid teamId, Guid callerSubjectId, NewTeamAccessLink link, CancellationToken ct) =>
        AsManagerAsync<Guid?>(teamId, callerSubjectId, null, async (db, token) =>
        {
            var entity = new TeamAccessLinkEntity
            {
                Id = Guid.CreateVersion7(),
                TeamId = teamId,
                CreatedBySubjectId = callerSubjectId,
                TokenHash = link.TokenHash,
                Label = link.Label,
                ExpiresAt = link.ExpiresAt,
                MaxUses = link.MaxUses,
                SuggestedRoleSlug = link.Suggestion.RoleSlug,
                SuggestedLimitTo24Hours = link.Suggestion.LimitTo24Hours,
                SuggestedAccessEndsAt = link.Suggestion.AccessEndsAt,
            };
            db.TeamAccessLinks.Add(entity);
            TeamAudit.StageAccessLinkCreated(db, teamId, entity.Id);
            await db.SaveChangesAsync(token);
            return entity.Id;
        }, ct);

    /// <inheritdoc/>
    public virtual Task<bool> RevokeAccessLinkAsync(
        Guid teamId, Guid callerSubjectId, Guid linkId, DateTime now, CancellationToken ct) =>
        RevokeLinkAsync(teamId, callerSubjectId, linkId, now, db => db.TeamAccessLinks, TeamAudit.StageAccessLinkRevoked, ct);

    /// <inheritdoc/>
    public virtual async Task<TeamAccessLinkInfo?> GetAccessLinkInfoAsync(string tokenHash, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var link = await db.TeamAccessLinks.AsNoTracking()
            .Where(l => l.TokenHash == tokenHash)
            .Select(l => new { Link = l, TeamName = l.Team!.Name, CreatedByName = l.CreatedBy!.Name })
            .FirstOrDefaultAsync(ct);
        if (link is null)
            return null;

        return new TeamAccessLinkInfo(
            link.Link.Id,
            link.Link.TeamId,
            link.TeamName,
            link.CreatedByName,
            SuggestionOf(link.Link),
            link.Link.ExpiresAt,
            await AccessLinkRefusalAsync(db, link.Link, now, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlySet<Guid>> ListSitesWithAccessAsync(
        Guid teamId, IReadOnlyCollection<Guid> tenantIds, CancellationToken ct)
    {
        if (tenantIds.Count == 0)
            return new HashSet<Guid>();

        await using var db = await contexts.CreatePlatformAsync(ct);
        var ids = tenantIds.ToList();
        return (await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId && ids.Contains(a.TenantId))
            .Select(a => a.TenantId)
            .ToListAsync(ct)).ToHashSet();
    }

    /// <summary>
    /// Gives the link's team access to <see cref="TeamAccessGrant.TenantId"/> and takes one use of the
    /// link, as <paramref name="approver"/>, on <paramref name="unit"/>, which must not be pinned to a
    /// tenant, and saves it. Returns with it the team reasons the team's staff now hold at the site
    /// (<see cref="TeamReasonChanges.SyncAsync"/>), none when it is refused.
    /// </summary>
    /// <remarks>
    /// Everything is decided under the site's owners lock, the team's manager lock and the site's team
    /// access lock, from what those locks hold still: the link, the approver's live membership of the
    /// site (an owner, and what its direct reason gives: the scopes it resolves to on the credential, its
    /// end and its 24-hour limit, never what a team reason beside it gives), and the chosen roles. Refused,
    /// taking no use and writing nothing, on any <see cref="TeamAccessRefusal"/>. Before those locks it
    /// locks the rows of the approver and of the team's staff (<see cref="MembershipLock"/>). Null, writing
    /// nothing, when a staff member joined between the two: the caller runs it again.
    /// </remarks>
    public static async Task<(TeamAccessApproval Approval, TeamReasonChanges? Changes)?> LockAccessApprovalAsync(
        DbUnitOfWork unit, string tokenHash, TeamAccessApproverCredential approver, TeamAccessGrant grant, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        var teamId = await db.TeamAccessLinks.AsNoTracking()
            .Where(l => l.TokenHash == tokenHash)
            .Select(l => (Guid?)l.TeamId)
            .FirstOrDefaultAsync(ct);
        if (teamId is not { } id)
            return (Refused(TeamAccessRefusal.NotFound), null);

        var locked = (await StaffSubjectsAsync(db, id, ct)).Append(approver.SubjectId).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTenantOwnersAsync(grant.TenantId, ct);
        await db.LockTeamManagersAsync(id, ct);
        await db.LockTeamAccessAsync(grant.TenantId, ct);

        var staffSubjects = await StaffSubjectsAsync(db, id, ct);
        if (!locked.IsSupersetOf(staffSubjects))
            return null;

        var link = await db.TeamAccessLinks.FirstOrDefaultAsync(l => l.TokenHash == tokenHash, ct);
        if (link is null)
            return (Refused(TeamAccessRefusal.NotFound), null);
        if (await AccessLinkRefusalAsync(db, link, now, ct) is { } refusal)
            return (Refused(refusal), null);

        if (await DirectOwnerAsync(db, grant.TenantId, approver.SubjectId, now, ct) is not (OwnerDecision.Decided, { } direct))
            return (Refused(TeamAccessRefusal.NotOwner), null);

        if (await db.TeamTenantAccess.AnyAsync(a => a.TeamId == id && a.TenantId == grant.TenantId, ct))
            return (Refused(TeamAccessRefusal.AlreadyGiven), null);

        var (roles, limited, rolesRefused) = await OwnerRolesAsync(db, grant.TenantId, grant.RoleIds, direct, approver, ct);
        if (rolesRefused is { } refused)
            return (Refused(refused), null);
        var suggested = RoleSeeds.Permissions[link.SuggestedRoleSlug];
        if (roles.Any(r => Scope.ValidateGrant(r.Permissions, suggested) is not null))
            return (Refused(TeamAccessRefusal.RoleWiderThanLink), null);
        if (TeamAccessRules.Refusal(SuggestionOf(link), limited, direct.AccessEndsAt, grant, now) is { } narrowing)
            return (Refused(narrowing), null);

        link.UseCount++;
        var access = new TeamTenantAccessEntity
        {
            Id = Guid.CreateVersion7(),
            TeamId = id,
            TenantId = grant.TenantId,
            LimitTo24Hours = grant.LimitTo24Hours,
            AccessEndsAt = grant.AccessEndsAt,
            ApprovedBySubjectId = approver.SubjectId,
            CreatedFromLinkId = link.Id,
            TeamManagesStaff = grant.TeamManagesStaff,
            AccessRoles = roles.Select(role => new TeamTenantAccessRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TenantRoleId = role.Id,
            }).ToList(),
        };
        db.TeamTenantAccess.Add(access);

        var staff = await db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == id && m.Kind == TeamMemberKind.Staff)
            .Select(m => new { m.Id, m.SubjectId })
            .ToListAsync(ct);
        foreach (var member in staff)
            db.TeamSiteStaff.Add(await NewSiteStaffAsync(db, access.Id, member.Id, member.SubjectId, grant.TeamManagesStaff, ct));

        var teamName = await db.Teams.Where(t => t.Id == id).Select(t => t.Name).FirstAsync(ct);
        TeamAudit.StageAccessGiven(db, access, teamName, roles.Select(r => r.Slug));
        await db.SaveChangesAsync(ct);
        return (new TeamAccessApproval(access.Id, null), (await TeamReasonChanges.SyncAsync(
            unit, staffSubjects.Select(subjectId => (grant.TenantId, subjectId)), now, ct)).WithStaffNotices(id, [grant.TenantId]));
    }

    /// <summary>
    /// Whether <paramref name="subjectId"/> is an owner of the active site <paramref name="tenantId"/> at
    /// <paramref name="now"/> by a direct reason in force, and what that reason gives: the one owner check
    /// of every owner's decision about a team, read under the site's owners lock the caller holds.
    /// <see cref="OwnerDecision.AccessFromTeam"/> when the subject is a member with no direct reason in
    /// force, who gives nothing, and <see cref="OwnerDecision.NotOwner"/> otherwise.
    /// </summary>
    /// <remarks>
    /// The owner role is held only directly, so an owner with no direct reason in force owns nothing.
    /// Approving the team's access is bounded further by what the direct reason gives
    /// (<see cref="TeamAccessRules"/>). A decision about the team's staff is not: it lets a staff member
    /// through to only what the access already gives, and an owner bounded that when they approved it.
    /// </remarks>
    internal static async Task<(OwnerDecision Outcome, TenantMemberAccess? Direct)> DirectOwnerAsync(
        NocturneDbContext db, Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct)
    {
        var membership = await db.TenantMembers.AsNoTracking()
            .LiveAt(now)
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId && m.Tenant!.IsActive)
            .Select(m => new
            {
                m.AccessEndsAt,
                m.LimitTo24Hours,
                m.DirectPermissions,
                Roles = m.MemberRoles.Select(r => new { r.TenantRole!.Slug, r.TenantRole.Permissions }).ToList(),
                Reasons = m.Reasons.AsQueryable().Select(MembershipReasonColumns.Of).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (membership is null)
            return (OwnerDecision.NotOwner, null);

        var row = new TenantMemberAccess(
            MemberPermissions.Effective(membership.Roles.Select(r => r.Permissions), membership.DirectPermissions).ToHashSet(),
            membership.LimitTo24Hours,
            membership.AccessEndsAt);
        if (MembershipReasonColumns.DirectAccess(membership.Reasons, row, now) is not { } direct)
            return (OwnerDecision.AccessFromTeam, null);

        return membership.Roles.Any(r => r.Slug == RoleSeeds.Owner)
            ? (OwnerDecision.Decided, direct)
            : (OwnerDecision.NotOwner, null);
    }

    /// <summary>
    /// Sets what the team's access to <paramref name="tenantId"/> gives to <paramref name="terms"/>, as
    /// <paramref name="editor"/>, on <paramref name="unit"/>, pinned to the tenant, and saves it. Returns with
    /// it the team reasons that bring the team's staff there into line with it
    /// (<see cref="TeamReasonChanges.SyncAsync"/>). Terms that change nothing write nothing.
    /// </summary>
    /// <remarks>
    /// Bounded as an approval is (<see cref="LockAccessApprovalAsync"/>), by the editor's direct reason and
    /// credential and never by the link the access was made from, since an edit can give the team more. It
    /// leaves each staff member's decision and the switch as they are, so it sends no one back to waiting.
    /// Takes the rows of the team's staff, the site's owners lock, the team's manager lock and the site's team
    /// access lock (<see cref="MembershipLock"/>). Null, writing nothing, when a staff member joined between
    /// the read of the staff and those locks: the caller runs it again.
    /// </remarks>
    public static async Task<(TeamAccessEdit Edit, TeamReasonChanges? Changes)?> LockAccessEditAsync(
        DbUnitOfWork unit, Guid tenantId, Guid teamId, TeamAccessApproverCredential editor, TeamAccessTerms terms, DateTime now,
        CancellationToken ct)
    {
        var db = unit.Context;
        var locked = (await StaffSubjectsAsync(db, teamId, ct)).ToHashSet();
        await db.LockSubjectRowsSharedAsync(locked, ct);
        await db.LockTenantOwnersAsync(tenantId, ct);
        await db.LockTeamManagersAsync(teamId, ct);
        await db.LockTeamAccessAsync(tenantId, ct);

        var staff = await StaffSubjectsAsync(db, teamId, ct);
        if (!locked.IsSupersetOf(staff))
            return null;

        var (owner, direct) = await DirectOwnerAsync(db, tenantId, editor.SubjectId, now, ct);
        if (direct is null)
            return (new TeamAccessEdit(owner, null), null);
        if (await AccessOfSiteAsync(db, tenantId, teamId, ct) is not { } access)
            return (new TeamAccessEdit(OwnerDecision.NotFound, null), null);

        var (roles, limited, refusal) = await OwnerRolesAsync(db, tenantId, terms.RoleIds, direct, editor, ct);
        if ((refusal ?? TeamAccessRules.Refusal(limited, direct.AccessEndsAt, terms.LimitTo24Hours, terms.AccessEndsAt, now)) is { } refused)
            return (new TeamAccessEdit(OwnerDecision.Decided, refused), null);

        var held = await db.TeamTenantAccessRoles
            .Where(r => r.TeamTenantAccessId == access.Id)
            .Select(r => new { Row = r, r.TenantRole!.Slug })
            .ToListAsync(ct);
        var before = (Roles: held.Select(h => h.Slug).ToList(), access.LimitTo24Hours, AccessEndsAt: Utc(access.AccessEndsAt));
        if (held.Select(h => h.Row.TenantRoleId).ToHashSet().SetEquals(roles.Select(r => r.Id))
            && before.LimitTo24Hours == terms.LimitTo24Hours
            && before.AccessEndsAt == terms.AccessEndsAt)
            return (new TeamAccessEdit(OwnerDecision.Decided, null), null);

        db.TeamTenantAccessRoles.RemoveRange(held.Where(h => roles.All(r => r.Id != h.Row.TenantRoleId)).Select(h => h.Row));
        foreach (var role in roles.Where(r => held.All(h => h.Row.TenantRoleId != r.Id)))
        {
            db.TeamTenantAccessRoles.Add(new TeamTenantAccessRoleEntity
            {
                Id = Guid.CreateVersion7(),
                TeamTenantAccessId = access.Id,
                TenantRoleId = role.Id,
            });
        }

        access.LimitTo24Hours = terms.LimitTo24Hours;
        access.AccessEndsAt = terms.AccessEndsAt;
        TeamAudit.StageAccessChanged(db, access, await TeamNameAsync(db, teamId, ct), roles.Select(r => r.Slug), before);
        await db.SaveChangesAsync(ct);
        return (new TeamAccessEdit(OwnerDecision.Decided, null), (await TeamReasonChanges.SyncAsync(
            unit, staff.Select(subjectId => (tenantId, subjectId)), now, ct)).WithStaffNotices(teamId, [tenantId]));
    }

    private sealed record OwnerRole(Guid Id, string Slug, List<string> Permissions);

    /// <summary>
    /// The roles of <paramref name="tenantId"/> that <paramref name="roleIds"/> name, and whether the owner
    /// reads only the last 24 hours (<see cref="TeamAccessRules.Approver"/>), or why the owner may not give
    /// them to a team: none chosen, one not the site's, one that runs the site, or one holding a permission
    /// the owner's direct reason <paramref name="direct"/> does not resolve to on <paramref name="credential"/>.
    /// </summary>
    private static async Task<(IReadOnlyList<OwnerRole> Roles, bool Limited, TeamAccessRefusal? Refusal)> OwnerRolesAsync(
        NocturneDbContext db, Guid tenantId, IReadOnlyList<Guid> roleIds, TenantMemberAccess direct,
        TeamAccessApproverCredential credential, CancellationToken ct)
    {
        var ids = roleIds.Distinct().ToList();
        if (ids.Count == 0)
            return ([], false, TeamAccessRefusal.NoRoles);

        var roles = await db.TenantRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId && ids.Contains(r.Id))
            .Select(r => new OwnerRole(r.Id, r.Slug, r.Permissions))
            .ToListAsync(ct);
        if (roles.Count != ids.Count)
            return ([], false, TeamAccessRefusal.RoleNotInTenant);
        if (roles.Any(r => !Scope.IsDataOnly(r.Permissions)))
            return ([], false, TeamAccessRefusal.RoleAdministrative);

        var (scopes, limited) = TeamAccessRules.Approver(direct.EffectivePermissions, direct.LimitTo24Hours, credential);
        return roles.Any(r => Scope.ValidateGrant(r.Permissions, scopes) is not null)
            ? ([], limited, TeamAccessRefusal.RoleWiderThanApprover)
            : (roles, limited, null);
    }

    private static Task<List<Guid>> StaffSubjectsAsync(NocturneDbContext db, Guid teamId, CancellationToken ct) =>
        db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Kind == TeamMemberKind.Staff)
            .Select(m => m.SubjectId)
            .ToListAsync(ct);

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TenantTeamAccess>> ListAccessOfTenantAsync(
        Guid tenantId, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        var rows = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.Team!.Name)
            .ThenBy(a => a.TeamId)
            .Select(a => new
            {
                a.TeamId,
                TeamName = a.Team!.Name,
                Roles = a.AccessRoles
                    .OrderBy(r => r.TenantRole!.Name)
                    .Select(r => new TenantTeamAccessRole(r.TenantRoleId, r.TenantRole!.Name, r.TenantRole.Slug))
                    .ToList(),
                a.LimitTo24Hours,
                a.AccessEndsAt,
                a.ApprovedBySubjectId,
                ApprovedByName = a.ApprovedBy == null ? null : a.ApprovedBy.Name,
                a.SysCreatedAt,
                a.TeamManagesStaff,
                Staff = db.TeamSiteStaff
                    .Where(s => s.TeamTenantAccessId == a.Id && s.TeamMember!.Kind == TeamMemberKind.Staff)
                    .OrderBy(s => s.Status != TeamSiteStaffStatus.Pending)
                    .ThenBy(s => s.TeamMember!.SysCreatedAt)
                    .ThenBy(s => s.TeamMemberId)
                    .Select(s => new
                    {
                        s.TeamMember!.SubjectId,
                        DisplayName = s.TeamMember.Subject!.Name,
                        s.Status,
                        s.TeamMember.RoleCeilingSlug,
                        s.TeamMember.LimitTo24Hours,
                        s.TeamMember.EndsAt,
                    })
                    .ToList(),
            })
            .ToListAsync(ct);
        return rows.Select(r => new TenantTeamAccess(
            r.TeamId,
            r.TeamName,
            r.Roles,
            r.LimitTo24Hours,
            Utc(r.AccessEndsAt),
            MembershipLiveness.HasEnded(r.AccessEndsAt, now),
            r.ApprovedBySubjectId,
            r.ApprovedByName,
            DateTime.SpecifyKind(r.SysCreatedAt, DateTimeKind.Utc),
            r.TeamManagesStaff,
            r.Staff.Select(s => new TenantTeamStaff(
                s.SubjectId,
                s.DisplayName,
                s.Status,
                new TeamStaffNarrowing(s.RoleCeilingSlug, s.LimitTo24Hours, Utc(s.EndsAt)),
                MembershipLiveness.HasEnded(s.EndsAt, now),
                TeamSuggestableRoles.NameOf(s.RoleCeilingSlug))).ToList())).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlySet<Guid>> ListRolesHeldByTeamsAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        return (await RolesHeldByTeams(db, tenantId).Distinct().ToListAsync(ct)).ToHashSet();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TeamAccessApprover>> ListAccessApproversAsync(Guid teamId, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.ApprovedBySubjectId != null)
            .OrderBy(a => a.TenantId)
            .Select(a => new TeamAccessApprover(a.TenantId, a.ApprovedBySubjectId!.Value, a.Team!.Name))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>The lock is <see cref="TeamStaffNoticeLock"/>, whose remarks say why it joins no lock order.</remarks>
    public virtual async Task WithStaffWaitingLockedAsync(
        Guid tenantId, Guid teamId, DateTime now, Func<TeamStaffWaiting?, Task> work, CancellationToken ct)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(contexts, tenantId, ct);
        await unit.CommitAsync(async token =>
        {
            await unit.Context.LockTeamStaffNoticesAsync(tenantId, teamId, token);
            var access = await unit.Context.TeamTenantAccess.AsNoTracking()
                .Where(a => a.TenantId == tenantId && a.TeamId == teamId)
                .Select(a => new { a.Id, TeamName = a.Team!.Name, a.AccessEndsAt })
                .FirstOrDefaultAsync(token);
            if (access is null)
            {
                await work(null);
                return;
            }

            var waiting = MembershipLiveness.HasEnded(access.AccessEndsAt, now)
                ? []
                : await unit.Context.TeamSiteStaff.AsNoTracking()
                    .Where(s => s.TeamTenantAccessId == access.Id
                        && s.Status == TeamSiteStaffStatus.Pending
                        && s.TeamMember!.Kind == TeamMemberKind.Staff
                        && (s.TeamMember.EndsAt == null || s.TeamMember.EndsAt > now))
                    .Select(s => s.TeamMember!.SubjectId)
                    .ToListAsync(token);
            await work(new TeamStaffWaiting(access.TeamName, waiting));
        }, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<TeamSiteKey>> ListStaffSitesOfSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        var pairs = await db.TeamSiteStaff.AsNoTracking()
            .Where(s => s.TeamMember!.SubjectId == subjectId && s.TeamMember.Kind == TeamMemberKind.Staff)
            .Select(s => new { s.TeamTenantAccess!.TenantId, s.TeamTenantAccess.TeamId })
            .Distinct()
            .ToListAsync(ct);
        return pairs.Select(p => new TeamSiteKey(p.TenantId, p.TeamId)).ToList();
    }

    /// <summary>The roles of <paramref name="tenantId"/> a team's access gives.</summary>
    internal static IQueryable<Guid> RolesHeldByTeams(NocturneDbContext db, Guid tenantId) =>
        db.TeamTenantAccessRoles.AsNoTracking()
            .Where(r => r.TeamTenantAccess!.TenantId == tenantId)
            .Select(r => r.TenantRoleId);

    private static TeamAccessApproval Refused(TeamAccessRefusal refusal) => new(null, refusal);

    private static TeamAccessSuggestion SuggestionOf(TeamAccessLinkEntity link) =>
        new(link.SuggestedRoleSlug, link.SuggestedLimitTo24Hours, Utc(link.SuggestedAccessEndsAt));

    private static DateTime? Utc(DateTime? instant) =>
        instant is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;

    /// <summary>
    /// Why an access link cannot be used at <paramref name="now"/>, or null when it can: as for any team
    /// link (<see cref="RefusalAsync"/>), and also once its suggested end has passed, since nothing it
    /// could give would last, or once its suggested role is no seed role this release knows.
    /// </summary>
    private static async Task<TeamAccessRefusal?> AccessLinkRefusalAsync(
        NocturneDbContext db, TeamAccessLinkEntity link, DateTime now, CancellationToken ct) =>
        await RefusalAsync(db, link, now, ct) switch
        {
            TeamInviteRefusal.Revoked => TeamAccessRefusal.Revoked,
            TeamInviteRefusal.Expired => TeamAccessRefusal.Expired,
            TeamInviteRefusal.UsedUp => TeamAccessRefusal.UsedUp,
            TeamInviteRefusal.NoLongerValid => TeamAccessRefusal.NoLongerValid,
            _ when !RoleSeeds.Permissions.ContainsKey(link.SuggestedRoleSlug) => TeamAccessRefusal.NoLongerValid,
            _ when link.SuggestedAccessEndsAt is { } end && MembershipLiveness.HasEnded(end, now) => TeamAccessRefusal.SuggestedEndPassed,
            _ => null,
        };

    private static async Task<IReadOnlyList<TeamAccessLinkSummary>> LiveAccessLinksAsync(
        NocturneDbContext db, Guid teamId, DateTime now, CancellationToken ct)
    {
        var links = await LiveLinks(db, db.TeamAccessLinks, teamId, now).AsNoTracking()
            .Where(l => l.SuggestedAccessEndsAt == null || l.SuggestedAccessEndsAt > now)
            .OrderBy(l => l.SysCreatedAt)
            .Select(l => new { Link = l, CreatedByName = l.CreatedBy!.Name })
            .ToListAsync(ct);
        return links.Select(l => new TeamAccessLinkSummary(
            l.Link.Id, l.Link.Label, l.Link.ExpiresAt, l.Link.MaxUses, l.Link.UseCount,
            l.Link.CreatedBySubjectId, l.CreatedByName, SuggestionOf(l.Link))).ToList();
    }

    private static async Task<IReadOnlyList<TeamSiteAccess>> SitesOfAsync(NocturneDbContext db, Guid teamId, DateTime now, CancellationToken ct)
    {
        var sites = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId)
            .OrderBy(a => a.Tenant!.DisplayName)
            .ThenBy(a => a.TenantId)
            .Select(a => new
            {
                a.TenantId,
                a.Tenant!.DisplayName,
                RoleNames = a.AccessRoles.OrderBy(r => r.TenantRole!.Name).Select(r => r.TenantRole!.Name).ToList(),
                a.LimitTo24Hours,
                a.AccessEndsAt,
                a.SysCreatedAt,
                a.TeamManagesStaff,
                Pending = db.TeamSiteStaff.Count(s => s.TeamTenantAccessId == a.Id && s.Status == TeamSiteStaffStatus.Pending),
                Declined = db.TeamSiteStaff.Count(s => s.TeamTenantAccessId == a.Id && s.Status == TeamSiteStaffStatus.Declined),
            })
            .ToListAsync(ct);
        return sites.Select(s => new TeamSiteAccess(
            s.TenantId, s.DisplayName, s.RoleNames, s.LimitTo24Hours, Utc(s.AccessEndsAt),
            MembershipLiveness.HasEnded(s.AccessEndsAt, now),
            DateTime.SpecifyKind(s.SysCreatedAt, DateTimeKind.Utc),
            s.TeamManagesStaff, s.Pending, s.Declined)).ToList();
    }

    /// <summary>
    /// The team's access to every site, with each access's role slugs, read inside the caller's
    /// transaction after taking each site's team access lock (<see cref="LockAccessOfTeamsAsync"/>), so a
    /// stop of one of them either commits first and is not read, or waits.
    /// </summary>
    private static async Task<IReadOnlyList<(TeamTenantAccessEntity Access, IReadOnlyList<string> RoleSlugs)>>
        AccessOfTeamAcrossSitesAsync(NocturneDbContext db, Guid teamId, CancellationToken ct)
    {
        await LockAccessOfTeamsAsync(db, [teamId], ct);

        var rows = await db.InTenantForTransactionAsync(Guid.Empty, token => db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId)
            .OrderBy(a => a.TenantId)
            .Select(a => new { Access = a, Slugs = a.AccessRoles.Select(r => r.TenantRole!.Slug).ToList() })
            .ToListAsync(token), ct);

        return rows.Select(r => (r.Access, (IReadOnlyList<string>)r.Slugs)).ToList();
    }

    /// <summary>
    /// Takes the team access lock (<see cref="TeamAccessLock"/>) of every site any of
    /// <paramref name="teamIds"/> has access to, in ascending tenant id, inside the caller's
    /// transaction. The caller holds the teams' manager locks, so no approval can add a site meanwhile.
    /// A context pinned to a tenant sees only that tenant's rows, so the read lifts the pin
    /// (<see cref="RlsPinningExtensions.InTenantForTransactionAsync{T}"/>).
    /// </summary>
    internal static async Task LockAccessOfTeamsAsync(NocturneDbContext db, IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
    {
        var sites = await db.InTenantForTransactionAsync(Guid.Empty, token => db.TeamTenantAccess.AsNoTracking()
            .Where(a => teamIds.Contains(a.TeamId))
            .Select(a => a.TenantId)
            .Distinct()
            .OrderBy(tenantId => tenantId)
            .ToListAsync(token), ct);

        foreach (var site in sites)
            await db.LockTeamAccessAsync(site, ct);
    }
}
