using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner of a site changing what a team's access gives in place, through <see cref="TeamAccessService"/>:
/// the staff's team reasons follow in the same commit, nobody is sent back to waiting, the decisions and the
/// switch stay, and the edit is bounded by the owner's direct reason and credential, never by the link.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private static readonly TeamAccessEdit Edited = new(OwnerDecision.Decided, null);

    private static TeamAccessApproverCredential SessionOf(Guid subject) =>
        new(subject, AuthType.SessionCookie, new HashSet<string>(), false);

    private Task<TeamAccessEdit> EditAsync(
        Site site, Team team, TeamAccessTerms terms, GuestSessionCacheService? sessions = null) =>
        EditAsAsync(site, team, SessionOf(site.Owner), terms, sessions);

    private Task<TeamAccessEdit> EditAsAsync(
        Site site, Team team, TeamAccessApproverCredential editor, TeamAccessTerms terms, GuestSessionCacheService? sessions = null) =>
        Service(site.Tenant, editor.SubjectId, sessions).EditAccessAsync(site.Tenant, team.Id, editor, terms, Ct);

    private void DismissNotices()
    {
        foreach (var notice in _notices.Live)
            notice.Archived = NotificationArchiveReason.Dismissed;
    }

    private async Task<DateTime?> GuestLinkExpiresAsync(Site site, Made made)
    {
        await using var db = Context(site.Tenant, null);
        return (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == made.Link, Ct)).ExpiresAt;
    }

    [Fact]
    public async Task A_narrowing_takes_from_staff_what_the_access_no_longer_gives_and_stops_what_a_member_it_leaves_with_nothing_made()
    {
        var site = await SiteAsync("edit-narrow");
        var team = await TeamAsync("Clinic");
        var treatments = await TreatmentsRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole, treatments]));
        var kept = await JoinStaffAsync(team);
        var ended = await NewSubjectAsync("ended");
        (await JoinAsync(team, ended, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null))).Refusal.Should().BeNull();
        (await ReasonAsync(site, ended, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
        var made = await MadeByAsync(site, ended);
        var sessions = SessionsOf(site, made.Link, made.Credential);
        DismissNotices();

        (await EditAsync(site, team, new TeamAccessTerms([treatments], false, null), sessions)).Should().Be(Edited);

        (await ReasonAsync(site, kept, access)).Should().BeEquivalentTo(new Held([treatments], null, false, null));
        await RowIsTheSumAsync(site, kept);
        (await ReasonAsync(site, ended, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, ended, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
        sessions.TryGet(site.Tenant, made.Link, out _).Should().BeFalse("the revoked guest link's session is evicted");
        (await StatusAsync(access, kept), await StatusAsync(access, ended))
            .Should().Be((TeamSiteStaffStatus.Approved, TeamSiteStaffStatus.Approved));
        _notices.Live.Should().BeEmpty("a narrowing gives nobody a first reason");
    }

    /// <remarks>
    /// The site decides its staff. The approved staff member already reaching the site gains the new role;
    /// the approved one whose ceiling shared nothing with the access reaches the site for the first time,
    /// which files the joined notice; the waiting one stays waiting with nothing.
    /// </remarks>
    [Fact]
    public async Task A_widening_reaches_approved_staff_within_their_ceiling_sends_nobody_to_waiting_and_files_one_joined_notice()
    {
        var site = await SiteAsync("edit-widen");
        var team = await TeamAsync("Clinic");
        var treatments = await TreatmentsRoleAsync(site);
        var access = await FollowAsync(team, site, Gives(site, [treatments], teamManagesStaff: false));
        var holding = await JoinStaffAsync(team);
        var first = await NewSubjectAsync("first");
        (await JoinAsync(team, first, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null))).Refusal.Should().BeNull();
        var waiting = await JoinStaffAsync(team);
        (await ApproveStaffAsync(site, team, site.Owner, holding)).Should().Be(OwnerDecision.Decided);
        (await ApproveStaffAsync(site, team, site.Owner, first)).Should().Be(OwnerDecision.Decided);
        (await ReasonAsync(site, first, access)).Should().BeNull();
        DismissNotices();

        (await EditAsync(site, team, new TeamAccessTerms([treatments, site.ViewerRole], false, null))).Should().Be(Edited);

        (await ReasonAsync(site, holding, access)).Should().BeEquivalentTo(new Held([treatments, site.ViewerRole], null, false, null));
        (await ReasonAsync(site, first, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
        await RowIsTheSumAsync(site, first);
        (await ReasonAsync(site, waiting, access)).Should().BeNull();
        (await StatusAsync(access, holding), await StatusAsync(access, first), await StatusAsync(access, waiting))
            .Should().Be((TeamSiteStaffStatus.Approved, TeamSiteStaffStatus.Approved, TeamSiteStaffStatus.Pending));
        LiveFor(site.Owner, TeamStaffAnnouncer.JoinedType).Should().ContainSingle().Which.Source.Should().Be(team.Id.ToString());
        CountOf(Waiting(site.Owner)).Should().Be(1);
    }

    [Fact]
    public async Task A_widening_where_every_staff_member_already_reaches_the_site_files_no_joined_notice()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("edit-widen-held");
        DismissNotices();

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], false, null))).Should().Be(Edited);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole, site.CaretakerRole], null, false, null));
        _notices.Live.Should().BeEmpty("the staff member reached the site before, so no one is new");
    }

    [Fact]
    public async Task Turning_the_limit_on_limits_staff_and_turning_it_off_lifts_it_except_for_staff_whose_own_narrowing_limits_them()
    {
        var (site, team, access, free) = await StaffWithReasonAsync("edit-limit");
        var narrowed = await NewSubjectAsync("narrowed");
        (await JoinAsync(team, narrowed, new TeamStaffNarrowing(null, true, null))).Refusal.Should().BeNull();

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole], true, null))).Should().Be(Edited);

        (await ReasonAsync(site, free, access))!.LimitTo24Hours.Should().BeTrue();
        (await ReasonAsync(site, narrowed, access))!.LimitTo24Hours.Should().BeTrue();

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole], false, null))).Should().Be(Edited);

        (await ReasonAsync(site, free, access))!.LimitTo24Hours.Should().BeFalse();
        (await ReasonAsync(site, narrowed, access))!.LimitTo24Hours.Should().BeTrue("the staff member's own narrowing limits them");
        await RowIsTheSumAsync(site, free);
        await RowIsTheSumAsync(site, narrowed);
    }

    [Fact]
    public async Task An_earlier_end_caps_what_staff_made_a_later_end_gives_a_lapsed_reason_back_and_a_passed_end_is_refused()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("edit-end");
        var made = await MadeByAsync(site, staff);
        var earlier = Now.AddDays(5);

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole], false, earlier))).Should().Be(Edited);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, earlier));
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!.AccessEndsAt.Should().Be(earlier);
        (await StateAsync(site, made)).Should().Be(CappedAt(earlier));
        (await GuestLinkExpiresAsync(site, made)).Should().Be(earlier);

        await SetAccessEndAsync(site, access, Passed);
        await SetReasonEndAsync(site, staff, access, Passed);
        var later = Now.AddDays(10);

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole], false, later))).Should().Be(Edited);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, later));
        (await Members(site.Tenant).IsMemberAsync(site.Tenant, staff, Now, Ct)).Should().BeTrue();
        await RowIsTheSumAsync(site, staff);
        (await RowsAsync(site.Tenant, staff)).Where(r => r.EventType == AuthAuditEventType.MemberJoined)
            .Should().HaveCount(2, "the history says the member has access through the team again")
            .And.OnlyContain(r => r.Team == team.Id);

        var audited = await AuditCountAsync(site);
        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole], false, Passed)))
            .Should().Be(new TeamAccessEdit(OwnerDecision.Decided, TeamAccessRefusal.EndInPast));
        (await AuditCountAsync(site)).Should().Be(audited);
        (await ReasonAsync(site, staff, access))!.AccessEndsAt.Should().Be(later);
    }

    [Fact]
    public async Task An_edit_leaves_declined_staff_declined_waiting_staff_waiting_and_the_switch_as_it_was()
    {
        var (site, team, access, waiting) = await WaitingStaffAsync("edit-declined", new TeamStaffNarrowing(null, false, null));
        var declined = await JoinStaffAsync(team);
        (await DeclineStaffAsync(site, team, site.Owner, declined)).Should().Be(OwnerDecision.Decided);

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], false, null))).Should().Be(Edited);

        (await StatusAsync(access, declined)).Should().Be(TeamSiteStaffStatus.Declined);
        (await DeclinesAsync(access, declined)).Should().Be(1);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, declined, Ct)).Should().BeNull();
        (await StatusAsync(access, waiting)).Should().Be(TeamSiteStaffStatus.Pending);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, waiting, Ct)).Should().BeNull();
        (await Teams().ListAccessOfTenantAsync(site.Tenant, Now, Ct)).Should().ContainSingle().Which.TeamManagesStaff.Should().BeFalse();
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_writes_nothing()
    {
        var site = await SiteAsync("edit-same");
        var team = await TeamAsync("Clinic");
        var end = Now.AddDays(4);
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole, site.CaretakerRole], limit: true, end: end));
        var staff = await JoinStaffAsync(team);
        var audited = await AuditCountAsync(site);
        var noticed = _notices.All.Count;
        var held = await ReasonAsync(site, staff, access);

        (await EditAsync(site, team, new TeamAccessTerms([site.CaretakerRole, site.ViewerRole, site.ViewerRole], true, end)))
            .Should().Be(Edited);

        (await AuditCountAsync(site)).Should().Be(audited);
        _notices.All.Should().HaveCount(noticed);
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(held);
    }

    [Fact]
    public async Task The_history_states_what_the_access_gives_after_the_change_and_the_managers_read_it_without_who_changed_it()
    {
        var (site, team, _, _) = await StaffWithReasonAsync("edit-history");
        var end = Now.AddDays(3);

        (await EditAsync(site, team, new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], true, end))).Should().Be(Edited);

        var entry = (await new MemberHistoryReader(Contexts(site.Tenant)).GetAsync(site.Tenant, null, 25, 0, Now, Ct)).Data
            .Should().ContainSingle(e => e.Kind == MemberHistoryKind.TeamAccessChanged).Subject;
        entry.Team!.Name.Should().Be(team.Name);
        entry.Member.Should().BeNull();
        entry.Actor.SubjectId.Should().Be(site.Owner);
        entry.Roles.Should().Equal(RoleSeeds.Caretaker, RoleSeeds.Viewer);
        entry.HistoryLimit.Should().Be(MemberHistoryLimit.TwentyFourHours);
        entry.AccessEndsAt.Should().Be(end);

        var details = await ReadAsync($"""
            SELECT concat_ws('|', details -> '{TeamAudit.OldRolesKey}', details ->> '{TeamAudit.OldHistoryLimitKey}',
                             coalesce(details ->> '{TeamAudit.OldAccessEndsAtKey}', 'none'))
              FROM auth_audit_log WHERE tenant_id = '{site.Tenant}' AND event_type = '{AuthAuditEventType.TeamAccessChanged}'
            """);
        details.Should().Equal($"[\"{RoleSeeds.Viewer}\"]|{MemberAccessAudit.FullHistory}|none");

        var managed = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!.Sites.Should().ContainSingle().Subject;
        managed.RoleNames.Should().BeEquivalentTo("Caretaker", "Viewer");
        (managed.LimitTo24Hours, managed.AccessEndsAt).Should().Be((true, end));
    }

    [Fact]
    public async Task Only_an_owner_by_a_direct_reason_edits_and_an_unknown_team_or_one_with_no_access_is_not_found()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("edit-who");
        var members = Members(site.Tenant);
        var admin = await NewSubjectAsync("admin");
        (await members.AddMemberAsync(
                new NewTenantMember(site.Tenant, admin, [(await members.FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Admin, Ct))!.Value])
                { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        var stranger = await TeamAsync("Ward");
        var wider = new TeamAccessTerms([site.ViewerRole, site.CaretakerRole], false, null);
        var audited = await AuditCountAsync(site);

        (await EditAsAsync(site, team, SessionOf(admin), wider)).Should().Be(new TeamAccessEdit(OwnerDecision.NotOwner, null));
        (await EditAsAsync(site, team, SessionOf(staff), wider)).Should().Be(new TeamAccessEdit(OwnerDecision.AccessFromTeam, null));
        (await EditAsync(site, new Team(Guid.NewGuid(), "Unknown", team.Manager), wider)).Should().Be(new TeamAccessEdit(OwnerDecision.NotFound, null));
        (await EditAsync(site, stranger, wider)).Should().Be(new TeamAccessEdit(OwnerDecision.NotFound, null));

        (await AuditCountAsync(site)).Should().Be(audited);
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
    }

    public static TheoryData<string, TeamAccessRefusal> RoleRefusals => new()
    {
        { "none", TeamAccessRefusal.NoRoles },
        { "administrative", TeamAccessRefusal.RoleAdministrative },
        { "another site's", TeamAccessRefusal.RoleNotInTenant },
    };

    [Theory]
    [MemberData(nameof(RoleRefusals))]
    public async Task Roles_the_owner_may_not_give_a_team_are_refused_and_change_nothing(string roles, TeamAccessRefusal refusal)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"edit-roles-{(int)refusal}");
        var other = await SiteAsync($"edit-roles-other-{(int)refusal}");
        IReadOnlyList<Guid> chosen = roles switch
        {
            "none" => [],
            "administrative" => [(await Members(site.Tenant).FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Admin, Ct))!.Value],
            _ => [site.ViewerRole, other.CaretakerRole],
        };
        var audited = await AuditCountAsync(site);

        (await EditAsync(site, team, new TeamAccessTerms(chosen, false, null))).Should().Be(new TeamAccessEdit(OwnerDecision.Decided, refusal));

        (await AuditCountAsync(site)).Should().Be(audited);
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));
    }

    /// <remarks>
    /// The owner holds every permission, but the token they edit with holds only reading glucose, or only the
    /// last 24 hours, and an edit can give the team more: it is bounded as an approval is.
    /// </remarks>
    [Fact]
    public async Task An_edit_is_bounded_by_the_owners_credential_its_scopes_and_its_24_hour_limit()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("edit-credential");
        var narrowToken = new TeamAccessApproverCredential(site.Owner, AuthType.OAuthAccessToken, new HashSet<string> { Scope.GlucoseRead }, false);
        var dayToken = new TeamAccessApproverCredential(site.Owner, AuthType.OAuthAccessToken, new HashSet<string> { Scope.FullAccess }, true);

        (await EditAsAsync(site, team, narrowToken, new TeamAccessTerms([site.CaretakerRole], false, null)))
            .Should().Be(new TeamAccessEdit(OwnerDecision.Decided, TeamAccessRefusal.RoleWiderThanApprover));
        (await EditAsAsync(site, team, dayToken, new TeamAccessTerms([site.ViewerRole], false, null)))
            .Should().Be(new TeamAccessEdit(OwnerDecision.Decided, TeamAccessRefusal.LimitRequired));
        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.ViewerRole], null, false, null));

        (await EditAsAsync(site, team, dayToken, new TeamAccessTerms([site.ViewerRole], true, null))).Should().Be(Edited);
        (await ReasonAsync(site, staff, access))!.LimitTo24Hours.Should().BeTrue();
    }

    [Fact]
    public async Task The_link_the_access_was_made_from_does_not_bound_the_edit()
    {
        var site = await SiteAsync("edit-link");
        var team = await TeamAsync("Clinic");
        var token = Guid.NewGuid().ToString("N");
        (await Teams(team.Manager).CreateAccessLinkAsync(team.Id, team.Manager, new NewTeamAccessLink(
            token, null, Now.AddDays(1), null, new TeamAccessSuggestion(RoleSeeds.Viewer, true, Now.AddDays(2))), Ct)).Should().NotBeNull();
        var access = (await ApproveAsync(site, token, Gives(site, [site.ViewerRole], limit: true, end: Now.AddDays(2)))).AccessId!.Value;
        var staff = await JoinStaffAsync(team);

        (await EditAsync(site, team, new TeamAccessTerms([site.CaretakerRole], false, null))).Should().Be(Edited);

        (await ReasonAsync(site, staff, access)).Should().BeEquivalentTo(new Held([site.CaretakerRole], null, false, null));
    }
}
