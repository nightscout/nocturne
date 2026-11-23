using Microsoft.EntityFrameworkCore;
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
        AsManagerAsync(teamId, callerSubjectId, false, async (db, token) =>
        {
            var link = await db.TeamAccessLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.TeamId == teamId, token);
            if (link is null)
                return false;

            if (link.RevokedAt is null)
            {
                link.RevokedAt = now;
                TeamAudit.StageAccessLinkRevoked(db, teamId, linkId);
                await db.SaveChangesAsync(token);
            }

            return true;
        }, ct);

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

    /// <inheritdoc/>
    public virtual async Task<TeamAccessApproval> ApproveAccessAsync(
        string tokenHash, TeamAccessApproverCredential approver, TeamAccessGrant grant, DateTime now, CancellationToken ct)
    {
        await using var db = await contexts.CreatePlatformAsync(ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockTenantOwnersAsync(grant.TenantId, token);

            var teamId = await db.TeamAccessLinks.AsNoTracking()
                .Where(l => l.TokenHash == tokenHash)
                .Select(l => (Guid?)l.TeamId)
                .FirstOrDefaultAsync(token);
            if (teamId is not { } id)
                return Refused(TeamAccessRefusal.NotFound);

            await db.LockTeamManagersAsync(id, token);
            await db.LockTeamAccessAsync(grant.TenantId, token);

            var link = await db.TeamAccessLinks.FirstOrDefaultAsync(l => l.TokenHash == tokenHash, token);
            if (link is null)
                return Refused(TeamAccessRefusal.NotFound);
            if (await AccessLinkRefusalAsync(db, link, now, token) is { } refusal)
                return Refused(refusal);

            var membership = await db.TenantMembers.AsNoTracking()
                .LiveAt(now)
                .Where(m => m.TenantId == grant.TenantId && m.SubjectId == approver.SubjectId && m.Tenant!.IsActive)
                .Select(m => new
                {
                    m.AccessEndsAt,
                    m.LimitTo24Hours,
                    m.DirectPermissions,
                    Roles = m.MemberRoles.Select(r => new { r.TenantRole!.Slug, r.TenantRole.Permissions }).ToList(),
                })
                .FirstOrDefaultAsync(token);
            if (membership is null || !membership.Roles.Any(r => r.Slug == RoleSeeds.Owner))
                return Refused(TeamAccessRefusal.NotOwner);

            if (await db.TeamTenantAccess.AnyAsync(a => a.TeamId == id && a.TenantId == grant.TenantId, token))
                return Refused(TeamAccessRefusal.AlreadyGiven);

            var roleIds = grant.RoleIds.Distinct().ToList();
            if (roleIds.Count == 0)
                return Refused(TeamAccessRefusal.NoRoles);

            var roles = await db.TenantRoles.AsNoTracking()
                .Where(r => r.TenantId == grant.TenantId && roleIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Slug, r.Permissions })
                .ToListAsync(token);
            if (roles.Count != roleIds.Count)
                return Refused(TeamAccessRefusal.RoleNotInTenant);
            if (roles.Any(r => !Scope.IsDataOnly(r.Permissions)))
                return Refused(TeamAccessRefusal.RoleAdministrative);
            var suggested = RoleSeeds.Permissions[link.SuggestedRoleSlug];
            if (roles.Any(r => Scope.ValidateGrant(r.Permissions, suggested) is not null))
                return Refused(TeamAccessRefusal.RoleWiderThanLink);

            var (scopes, limited) = TeamAccessRules.Approver(
                MemberPermissions.Effective(membership.Roles.Select(r => r.Permissions), membership.DirectPermissions),
                membership.LimitTo24Hours,
                approver);
            if (roles.Any(r => Scope.ValidateGrant(r.Permissions, scopes) is not null))
                return Refused(TeamAccessRefusal.RoleWiderThanApprover);
            if (TeamAccessRules.Refusal(SuggestionOf(link), limited, membership.AccessEndsAt, grant, now) is { } narrowing)
                return Refused(narrowing);

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
                AccessRoles = roleIds.Select(roleId => new TeamTenantAccessRoleEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantRoleId = roleId,
                }).ToList(),
            };
            db.TeamTenantAccess.Add(access);

            var teamName = await db.Teams.Where(t => t.Id == id).Select(t => t.Name).FirstAsync(token);
            TeamAudit.StageAccessGiven(db, access, teamName, roles.Select(r => r.Slug));
            await db.SaveChangesAsync(token);
            return new TeamAccessApproval(access.Id, null);
        }, ct: ct);
    }

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
            DateTime.SpecifyKind(r.SysCreatedAt, DateTimeKind.Utc))).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> StopAccessAsync(Guid tenantId, Guid teamId, CancellationToken ct)
    {
        await using var db = await contexts.CreateForTenantAsync(tenantId, ct);
        return await db.ExecuteInTransactionAsync(async token =>
        {
            await db.LockTeamAccessAsync(tenantId, token);

            var access = await db.TeamTenantAccess
                .Include(a => a.AccessRoles).ThenInclude(r => r.TenantRole)
                .Include(a => a.Team)
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.TeamId == teamId, token);
            if (access is null)
                return false;

            TeamAudit.StageAccessStopped(
                db, access, access.Team!.Name, access.AccessRoles.Select(r => r.TenantRole!.Slug), TeamAudit.StoppedHow);
            db.TeamTenantAccess.Remove(access);
            await db.SaveChangesAsync(token);
            return true;
        }, ct: ct);
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
            })
            .ToListAsync(ct);
        return sites.Select(s => new TeamSiteAccess(
            s.TenantId, s.DisplayName, s.RoleNames, s.LimitTo24Hours, Utc(s.AccessEndsAt),
            MembershipLiveness.HasEnded(s.AccessEndsAt, now),
            DateTime.SpecifyKind(s.SysCreatedAt, DateTimeKind.Utc))).ToList();
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

        var pinned = db.TenantId != Guid.Empty && db.Database.IsNpgsql();
        if (pinned)
            await SetTenantForTransactionAsync(db, string.Empty, ct);

        var rows = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => a.TeamId == teamId)
            .OrderBy(a => a.TenantId)
            .Select(a => new { Access = a, Slugs = a.AccessRoles.Select(r => r.TenantRole!.Slug).ToList() })
            .ToListAsync(ct);

        if (pinned)
            await SetTenantForTransactionAsync(db, db.TenantId.ToString(), ct);

        return rows.Select(r => (r.Access, (IReadOnlyList<string>)r.Slugs)).ToList();
    }

    /// <summary>
    /// Takes the team access lock (<see cref="TeamAccessLock"/>) of every site any of
    /// <paramref name="teamIds"/> has access to, in ascending tenant id, inside the caller's
    /// transaction. The caller holds the teams' manager locks, so no approval can add a site meanwhile.
    /// A context pinned to a tenant sees only that tenant's rows, so the pin is lifted for the read and
    /// restored after it, both local to the transaction.
    /// </summary>
    internal static async Task LockAccessOfTeamsAsync(NocturneDbContext db, IReadOnlyCollection<Guid> teamIds, CancellationToken ct)
    {
        var pinned = db.TenantId != Guid.Empty && db.Database.IsNpgsql();
        if (pinned)
            await SetTenantForTransactionAsync(db, string.Empty, ct);

        var sites = await db.TeamTenantAccess.AsNoTracking()
            .Where(a => teamIds.Contains(a.TeamId))
            .Select(a => a.TenantId)
            .Distinct()
            .OrderBy(tenantId => tenantId)
            .ToListAsync(ct);

        if (pinned)
            await SetTenantForTransactionAsync(db, db.TenantId.ToString(), ct);

        foreach (var site in sites)
            await db.LockTeamAccessAsync(site, ct);
    }

    private static Task SetTenantForTransactionAsync(NocturneDbContext db, string tenantId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT set_config('app.current_tenant_id', {0}, true)", [tenantId], ct);
}
