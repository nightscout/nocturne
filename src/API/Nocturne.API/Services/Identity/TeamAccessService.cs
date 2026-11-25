using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// A person giving a team access to a site through the team's link: what the link offers them, site
/// by site, and the approval. Only an owner of the site may approve, and never wider than the link,
/// than the roles they could grant, or than their own access. Also the other team writes that change
/// the team reasons of its staff (<see cref="TeamReasonWrites"/>): a staff member joining, being edited,
/// being removed or leaving, a site stopping the team's access, the team being deleted, and an owner of
/// a site changing what the access gives or deciding which of the team's staff reach it. Each of those, once committed, files or takes back
/// the notices the owners of the sites are owed (<see cref="TeamStaffAnnouncer"/>).
/// </summary>
/// <remarks>
/// The approver is read from their membership of the chosen site, never from the request's tenant:
/// the link is opened on any host. These checks only refuse early: the approval's own transaction
/// decides again under the locks (<see cref="TeamRepository.LockAccessApprovalAsync"/>), since the
/// approver's membership and the roles can change meanwhile.
/// </remarks>
public sealed class TeamAccessService(
    ITeamRepository teams,
    ITenantMembershipRepository members,
    ITenantRoleService roles,
    TimeProvider time,
    INocturneDbContextFactory contexts,
    GrantRevocationService grantRevocation,
    TeamStaffAnnouncer announcer)
{
    /// <summary>
    /// The link's team and suggestion, and the caller's sites they may use it for. Null when there is
    /// no such link; a link that cannot be used carries only its refusal.
    /// </summary>
    public async Task<(TeamAccessLinkDetails? Details, TeamAccessRefusal? Refusal)> GetLinkAsync(
        string tokenHash, TeamAccessApproverCredential caller, CancellationToken ct)
    {
        var now = Now;
        var link = await teams.GetAccessLinkInfoAsync(tokenHash, now, ct);
        if (link is null)
            return (null, TeamAccessRefusal.NotFound);
        if (link.Refusal is { } refusal)
            return (null, refusal);

        var sites = await OwnedSitesAsync(caller, now, ct);
        var given = await teams.ListSitesWithAccessAsync(link.TeamId, sites.Select(s => s.Tenant.Id).ToList(), ct);
        var suggested = RoleSeeds.Permissions[link.Suggestion.RoleSlug];

        var options = new List<TeamAccessSiteOption>();
        foreach (var site in sites)
        {
            var offered = (await roles.GetRolesAsync(site.Tenant.Id, site.Scopes, ct))
                .Where(r => TeamAccessRules.MayGiveToTeam(r) && Scope.ValidateGrant(r.Permissions, suggested) is null)
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new TeamAccessRoleOption(r.Id, r.Name, r.Slug, r.Permissions))
                .ToList();
            options.Add(new TeamAccessSiteOption(
                site.Tenant.Id,
                site.Tenant.DisplayName,
                given.Contains(site.Tenant.Id),
                offered,
                offered.FirstOrDefault(r => r.Slug == link.Suggestion.RoleSlug)?.Id,
                link.Suggestion.LimitTo24Hours || site.Limited,
                Earliest(link.Suggestion.AccessEndsAt, site.Membership.AccessEndsAt)));
        }

        return (new TeamAccessLinkDetails(
            link.TeamId,
            link.TeamName,
            link.CreatedByName,
            link.Suggestion.RoleSlug,
            suggested,
            link.Suggestion.LimitTo24Hours,
            link.Suggestion.AccessEndsAt,
            link.ExpiresAt,
            options), null);
    }

    /// <summary>Gives the link's team access to <see cref="TeamAccessGrant.TenantId"/>, as the caller.</summary>
    public async Task<TeamAccessApproval> ApproveAsync(
        string tokenHash, TeamAccessApproverCredential caller, TeamAccessGrant grant, CancellationToken ct)
    {
        var now = Now;
        var link = await teams.GetAccessLinkInfoAsync(tokenHash, now, ct);
        if (link is null)
            return new TeamAccessApproval(null, TeamAccessRefusal.NotFound);
        if (link.Refusal is { } refusal)
            return new TeamAccessApproval(null, refusal);

        var site = (await OwnedSitesAsync(caller, now, ct)).FirstOrDefault(s => s.Tenant.Id == grant.TenantId);
        if (site is null)
            return new TeamAccessApproval(null, TeamAccessRefusal.NotOwner);
        if (grant.RoleIds.Count == 0)
            return new TeamAccessApproval(null, TeamAccessRefusal.NoRoles);

        var validation = await roles.ValidateRoleGrantAsync(grant.TenantId, grant.RoleIds, site.Scopes, ct);
        if (!validation.Ok)
            return new TeamAccessApproval(null, validation.ErrorCode == RoleGrantValidation.ForeignRole
                ? TeamAccessRefusal.RoleNotInTenant
                : TeamAccessRefusal.RoleWiderThanApprover);

        if (TeamAccessRules.Refusal(link.Suggestion, site.Limited, site.Membership.AccessEndsAt, grant, now) is { } narrowing)
            return new TeamAccessApproval(null, narrowing);

        return await AnnouncedAsync(TeamReasonWrites.ApproveAccessAsync(contexts, grantRevocation, tokenHash, caller, grant, now, ct));
    }

    /// <inheritdoc cref="TeamRepository.LockAccessEditAsync"/>
    public Task<TeamAccessEdit> EditAccessAsync(
        Guid tenantId, Guid teamId, TeamAccessApproverCredential editor, TeamAccessTerms terms, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.EditAccessAsync(contexts, grantRevocation, tenantId, teamId, editor, terms, Now, ct));

    /// <inheritdoc cref="TeamReasonWrites.StopAsync"/>
    public Task<bool> StopAsync(Guid tenantId, Guid teamId, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.StopAsync(contexts, grantRevocation, tenantId, teamId, Now, ct));

    /// <inheritdoc cref="TeamReasonWrites.DeleteTeamAsync"/>
    public Task<TeamDeletion> DeleteTeamAsync(Guid teamId, Guid callerSubjectId, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.DeleteTeamAsync(contexts, grantRevocation, teamId, callerSubjectId, Now, ct));

    /// <inheritdoc cref="TeamReasonWrites.RemoveStaffAsync"/>
    public Task<bool> RemoveStaffAsync(Guid teamId, Guid callerSubjectId, Guid staffSubjectId, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.RemoveStaffAsync(contexts, grantRevocation, teamId, callerSubjectId, staffSubjectId, Now, ct));

    /// <inheritdoc cref="TeamRepository.LockStaffJoinAsync"/>
    public Task<TeamInviteAcceptance> JoinStaffAsync(string tokenHash, Guid subjectId, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.JoinStaffAsync(contexts, grantRevocation, tokenHash, subjectId, Now, ct));

    /// <inheritdoc cref="TeamRepository.LockStaffUpdateAsync"/>
    public Task<TeamStaffChange> UpdateStaffAsync(
        Guid teamId, Guid callerSubjectId, Guid staffSubjectId, TeamStaffNarrowing narrowing, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.UpdateStaffAsync(contexts, grantRevocation, teamId, callerSubjectId, staffSubjectId, narrowing, Now, ct));

    /// <summary>
    /// An owner of <paramref name="tenantId"/> approves a staff member of the team there
    /// (<see cref="TeamRepository.LockStaffDecisionAsync"/>).
    /// </summary>
    public Task<OwnerDecision> ApproveStaffAsync(
        Guid tenantId, Guid teamId, Guid callerSubjectId, Guid staffSubjectId, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.DecideStaffAsync(
            contexts, grantRevocation, tenantId, teamId, callerSubjectId, staffSubjectId, TeamSiteStaffStatus.Approved, Now, ct));

    /// <summary>
    /// An owner of <paramref name="tenantId"/> declines a staff member of the team there
    /// (<see cref="TeamRepository.LockStaffDecisionAsync"/>).
    /// </summary>
    public Task<OwnerDecision> DeclineStaffAsync(
        Guid tenantId, Guid teamId, Guid callerSubjectId, Guid staffSubjectId, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.DecideStaffAsync(
            contexts, grantRevocation, tenantId, teamId, callerSubjectId, staffSubjectId, TeamSiteStaffStatus.Declined, Now, ct));

    /// <inheritdoc cref="TeamRepository.LockStaffRuleAsync"/>
    public Task<OwnerDecision> SetStaffRuleAsync(
        Guid tenantId, Guid teamId, Guid callerSubjectId, bool teamManagesStaff, CancellationToken ct) =>
        AnnouncedAsync(TeamReasonWrites.SetStaffRuleAsync(contexts, grantRevocation, tenantId, teamId, callerSubjectId, teamManagesStaff, Now, ct));

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private async Task<T> AnnouncedAsync<T>(Task<TeamReasonWrites.Written<T>> write)
    {
        var written = await write;
        await announcer.AnnounceAsync(written.Notices);
        return written.Outcome;
    }

    private static DateTime? Earliest(DateTime? a, DateTime? b) =>
        a is { } x && b is { } y ? (x < y ? x : y) : a ?? b;

    /// <summary>
    /// The active sites where the caller's membership is in force and holds the owner role, with the
    /// scopes it resolves to on the caller's credential and whether it reads only the last 24 hours.
    /// </summary>
    /// <remarks>
    /// Read from the row, the sum of the caller's reasons, since this subject-pinned read cannot see
    /// them. The owner role is only ever held directly, so the sum differs from the direct reason only
    /// in an end a team reason outlasts; the approval holds the approver to the direct reason
    /// (<see cref="TeamRepository.LockAccessApprovalAsync"/>).
    /// </remarks>
    private async Task<IReadOnlyList<OwnedSite>> OwnedSitesAsync(
        TeamAccessApproverCredential caller, DateTime now, CancellationToken ct) =>
        (await members.ListMembershipsOfSubjectAsync(caller.SubjectId, now, ct))
            .Where(m => m.Tenant.IsActive && m.Membership.IsOwner)
            .OrderBy(m => m.Tenant.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Tenant.Id)
            .Select(m =>
            {
                var (scopes, limited) = TeamAccessRules.Approver(
                    m.Membership.EffectivePermissions(), m.Membership.LimitTo24Hours, caller);
                return new OwnedSite(m.Tenant, m.Membership, scopes, limited);
            })
            .ToList();

    private sealed record OwnedSite(TenantRecord Tenant, TenantMemberRecord Membership, IReadOnlySet<string> Scopes, bool Limited);
}

/// <summary>
/// A "give this team access" link as the person holding it reads it: the team, what the link suggests
/// (<paramref name="SuggestedRolePermissions"/> being the suggested role's permissions as every new site
/// is seeded with it), and in <paramref name="Sites"/> the sites the person owns, the only ones they
/// may give access to. Never the team's managers or anyone else's sites.
/// </summary>
public sealed record TeamAccessLinkDetails(
    Guid TeamId,
    string TeamName,
    string CreatedByName,
    string SuggestedRoleSlug,
    IReadOnlyList<string> SuggestedRolePermissions,
    bool SuggestedLimitTo24Hours,
    DateTime? SuggestedAccessEndsAt,
    DateTime ExpiresAt,
    IReadOnlyList<TeamAccessSiteOption> Sites);

/// <summary>
/// One of the person's sites, and what they may give the team there. <paramref name="AlreadyGiven"/>
/// says the team already has access, so it cannot be given again. <paramref name="Roles"/> are the
/// site's roles that may be chosen: data only, within the link's suggestion and within what the person
/// may grant; <paramref name="DefaultRoleId"/> is the one with the suggested slug, when it may be
/// chosen. <paramref name="LimitForced"/> says the access must be limited to the last 24 hours, and
/// <paramref name="LatestEnd"/> is the latest it may end, in UTC, null when it need not end.
/// </summary>
public sealed record TeamAccessSiteOption(
    Guid TenantId,
    string DisplayName,
    bool AlreadyGiven,
    IReadOnlyList<TeamAccessRoleOption> Roles,
    Guid? DefaultRoleId,
    bool LimitForced,
    DateTime? LatestEnd);

/// <summary>A role of a site that may be given to a team.</summary>
public sealed record TeamAccessRoleOption(Guid Id, string Name, string Slug, IReadOnlyList<string> Permissions);
