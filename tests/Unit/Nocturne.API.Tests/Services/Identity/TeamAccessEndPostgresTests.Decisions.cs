using FluentAssertions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner of a site deciding which of a team's staff reach it: approving and declining a staff member,
/// and turning "the team manages its own staff" off and on, with the team reasons each change brings into
/// line in its commit, the decline that holds against every team edit, and who may decide.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private Task<OwnerDecision> ApproveStaffAsync(Site site, Team team, Guid caller, Guid staff) =>
        Service(site.Tenant, caller).ApproveStaffAsync(site.Tenant, team.Id, caller, staff, Ct);

    private Task<OwnerDecision> DeclineStaffAsync(Site site, Team team, Guid caller, Guid staff) =>
        Service(site.Tenant, caller).DeclineStaffAsync(site.Tenant, team.Id, caller, staff, Ct);

    private Task<OwnerDecision> SetStaffRuleAsync(Site site, Team team, bool teamManagesStaff) =>
        Service(site.Tenant, site.Owner).SetStaffRuleAsync(site.Tenant, team.Id, site.Owner, teamManagesStaff, Ct);

    private async Task<string?> StatusAsync(Guid access, Guid staff) =>
        (await ReadAsync($"""
            SELECT s.status FROM team_site_staff s JOIN team_members m ON m.id = s.team_member_id
             WHERE s.team_tenant_access_id = '{access}' AND m.subject_id = '{staff}'
            """)).SingleOrDefault();

    private async Task<int> DeclinesAsync(Guid access, Guid staff) =>
        int.Parse((await ReadAsync(
            $"SELECT count(*)::text FROM team_site_declines WHERE team_tenant_access_id = '{access}' AND subject_id = '{staff}'")).Single());

    private async Task<List<string>> RuleRowsAsync(Site site) =>
        await ReadAsync($"""
            SELECT details ->> '{TeamAudit.TeamManagesStaffKey}' FROM auth_audit_log
             WHERE tenant_id = '{site.Tenant}' AND event_type = '{AuthAuditEventType.TeamStaffRuleChanged}' AND target_subject_id IS NULL
             ORDER BY created_at, id
            """);

    /// <summary>A site, a team following it that decides its staff there, and a staff member holding the reason it gives.</summary>
    private async Task<(Site Site, Team Team, Guid Access, Guid Staff)> ApprovedStaffAsync(string slug, TeamStaffNarrowing narrowing)
    {
        var (site, team, access, staff) = await SiteAndTeamAsync(slug);
        (await JoinAsync(team, staff, narrowing)).Refusal.Should().BeNull();
        await HoldsTeamReasonAsync(site, staff, access);
        return (site, team, access, staff);
    }

    [Fact]
    public async Task Turning_the_switch_off_keeps_approved_staff_and_new_and_widened_staff_wait_with_no_reason()
    {
        var (site, team, access, kept) = await ApprovedStaffAsync("switch-off", new TeamStaffNarrowing(null, false, null));
        var widened = await NewSubjectAsync("widened");
        (await JoinAsync(team, widened, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null))).Refusal.Should().BeNull();
        await HoldsTeamReasonAsync(site, widened, access);

        (await SetStaffRuleAsync(site, team, false)).Should().Be(OwnerDecision.Decided);

        await HoldsTeamReasonAsync(site, kept, access);
        (await StatusAsync(access, kept)).Should().Be(TeamSiteStaffStatus.Approved);
        (await RuleRowsAsync(site)).Should().Equal("false");

        var joiner = await JoinStaffAsync(team);
        (await StatusAsync(access, joiner)).Should().Be(TeamSiteStaffStatus.Pending);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, joiner, Ct)).Should().BeNull();

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, widened, new TeamStaffNarrowing(null, false, null), Ct))
            .Should().Be(TeamStaffChange.Changed);
        (await StatusAsync(access, widened)).Should().Be(TeamSiteStaffStatus.Pending);
        (await ReasonAsync(site, widened, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, widened, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Turning_the_switch_on_approves_every_waiting_staff_member_in_one_commit_and_keeps_the_declined()
    {
        var site = await SiteAsync("switch-on");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var first = await JoinStaffAsync(team);
        var second = await JoinStaffAsync(team);
        var declined = await JoinStaffAsync(team);
        (await DeclineStaffAsync(site, team, site.Owner, declined)).Should().Be(OwnerDecision.Decided);

        await using (var holder = Context(site.Tenant, null))
        {
            await using var held = await holder.Database.BeginTransactionAsync(Ct);
            await holder.LockMembershipAsync(site.Tenant, second, Ct);
            var switchOn = SetStaffRuleAsync(site, team, true);
            await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, switchOn);

            (await StatusAsync(access, first)).Should().Be(TeamSiteStaffStatus.Pending, "nothing of the switch is seen before its reasons are written");
            (await RuleRowsAsync(site)).Should().BeEmpty();
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, first, Ct)).Should().BeNull();

            await held.RollbackAsync(Ct);
            (await switchOn.WaitAsync(Eventually.DefaultPatience)).Should().Be(OwnerDecision.Decided);
        }

        foreach (var staff in new[] { first, second })
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
            await HoldsTeamReasonAsync(site, staff, access);
            (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.TeamStaffApproved)
                .Which.Team.Should().Be(team.Id);
        }

        (await StatusAsync(access, declined)).Should().Be(TeamSiteStaffStatus.Declined);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, declined, Ct)).Should().BeNull();
        (await RowsAsync(site.Tenant, declined)).Should().NotContain(r => r.EventType == AuthAuditEventType.TeamStaffApproved);
        (await RuleRowsAsync(site)).Should().Equal("true");
    }

    [Fact]
    public async Task A_switch_already_at_the_value_changes_nothing_and_records_nothing()
    {
        var (site, team, _, _) = await StaffWithReasonAsync("switch-same");
        var audited = await AuditCountAsync(site);

        (await SetStaffRuleAsync(site, team, true)).Should().Be(OwnerDecision.Decided);

        (await AuditCountAsync(site)).Should().Be(audited);
    }

    [Fact]
    public async Task Declining_an_approved_staff_member_ends_their_reason_and_stops_what_they_made_and_the_rest_keep_theirs()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("decline-approved");
        var kept = await JoinStaffAsync(team);
        var made = await MadeByAsync(site, staff);
        var sessions = SessionsOf(site, made.Link, made.Credential);

        (await Service(site.Tenant, site.Owner, sessions).DeclineStaffAsync(site.Tenant, team.Id, site.Owner, staff, Ct))
            .Should().Be(OwnerDecision.Decided);

        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Declined);
        (await DeclinesAsync(access, staff)).Should().Be(1);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
        sessions.TryGet(site.Tenant, made.Link, out _).Should().BeFalse("the revoked guest link's session is evicted");
        var rows = await RowsAsync(site.Tenant, staff);
        rows.Should().ContainSingle(r => r.EventType == AuthAuditEventType.TeamStaffDeclined).Which.Read(TeamAudit.NameKey).Should().Be(team.Name);
        rows.Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved);
        await HoldsTeamReasonAsync(site, kept, access);
    }

    [Fact]
    public async Task A_decline_holds_against_every_team_edit_a_rejoin_and_the_switch_and_only_an_approval_lifts_it()
    {
        var (site, team, access, staff) = await ApprovedStaffAsync("decline-holds", new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));
        (await DeclineStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var manager = Service(Guid.Empty, team.Manager);

        async Task StillDeclinedAsync(string after)
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Declined, after);
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull(after);
        }

        (await manager.UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, false, null), Ct)).Should().Be(TeamStaffChange.Changed);
        await StillDeclinedAsync("a widening");
        (await manager.UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, true, null), Ct)).Should().Be(TeamStaffChange.Changed);
        await StillDeclinedAsync("a narrowing");
        (await manager.RemoveStaffAsync(team.Id, team.Manager, staff, Ct)).Should().BeTrue();
        (await DeclinesAsync(access, staff)).Should().Be(1, "the decline outlasts the team membership");
        await JoinStaffAsync(team, staff);
        await StillDeclinedAsync("a removal and a new invite");
        (await SetStaffRuleAsync(site, team, false)).Should().Be(OwnerDecision.Decided);
        (await SetStaffRuleAsync(site, team, true)).Should().Be(OwnerDecision.Decided);
        await StillDeclinedAsync("the switch turned on");

        (await ApproveStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        (await DeclinesAsync(access, staff)).Should().Be(0);
        await HoldsTeamReasonAsync(site, staff, access);
        (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.TeamStaffApproved);
    }

    [Fact]
    public async Task Approving_a_waiting_staff_member_gives_the_reason_and_approving_again_records_nothing()
    {
        var site = await SiteAsync("approve-waiting");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var staff = await JoinStaffAsync(team);

        (await ApproveStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        await HoldsTeamReasonAsync(site, staff, access);
        var audited = await AuditCountAsync(site);
        (await ApproveStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await AuditCountAsync(site)).Should().Be(audited);
    }

    [Fact]
    public async Task A_site_that_stops_the_team_and_approves_it_again_decides_afresh()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("stop-again");
        (await DeclineStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        (await Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct)).Should().BeTrue();
        (await DeclinesAsync(access, staff)).Should().Be(0);
        var again = await FollowAsync(team, site);

        (await StatusAsync(again, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        await HoldsTeamReasonAsync(site, staff, again);
    }

    [Fact]
    public async Task The_startup_check_removes_the_team_reason_of_a_declined_row()
    {
        var (site, _, access, staff) = await StaffWithReasonAsync("check-declined");
        await NpgsqlTestCommands.ExecuteAsync(
            database.MigratorConnectionString, $"UPDATE team_site_staff SET status = 'declined' WHERE team_tenant_access_id = '{access}'");

        await ReconcileAsync();

        (await ReasonAsync(site, staff, access)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Only_an_owner_by_a_direct_reason_decides_and_an_unknown_team_or_staff_member_is_not_found()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("who-decides");
        var viewer = await NewSubjectAsync("viewer");
        await DirectMemberAsync(site, viewer, endsAt: null);
        var other = await JoinStaffAsync(team);
        var lapsedStaff = await SecondOwnerAsync(site);
        await LapseDirectReasonAsync(site, lapsedStaff);
        await JoinStaffAsync(team, lapsedStaff);
        var lapsed = await SecondOwnerAsync(site);
        await LapseDirectReasonAsync(site, lapsed);

        (await DeclineStaffAsync(site, team, viewer, staff)).Should().Be(OwnerDecision.NotOwner);
        (await DeclineStaffAsync(site, team, other, staff)).Should().Be(OwnerDecision.AccessFromTeam);
        (await DeclineStaffAsync(site, team, lapsedStaff, staff)).Should().Be(OwnerDecision.AccessFromTeam, "the owner's direct reason has ended and only the team keeps them");
        (await DeclineStaffAsync(site, team, lapsed, staff)).Should().Be(OwnerDecision.NotOwner, "the owner's direct reason has ended and nothing else keeps them a member");
        (await Service(site.Tenant, viewer).SetStaffRuleAsync(site.Tenant, team.Id, viewer, false, Ct)).Should().Be(OwnerDecision.NotOwner);
        (await Service(site.Tenant, other).SetStaffRuleAsync(site.Tenant, team.Id, other, false, Ct)).Should().Be(OwnerDecision.AccessFromTeam);
        (await DeclineStaffAsync(site, new Team(Guid.NewGuid(), "Unknown", team.Manager), site.Owner, staff)).Should().Be(OwnerDecision.NotFound);
        (await DeclineStaffAsync(site, team, site.Owner, viewer)).Should().Be(OwnerDecision.NotFound);
        (await DeclineStaffAsync(site, team, site.Owner, team.Manager)).Should().Be(OwnerDecision.NotFound);
        (await SetStaffRuleAsync(site, new Team(Guid.NewGuid(), "Unknown", team.Manager), false)).Should().Be(OwnerDecision.NotFound);

        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        await HoldsTeamReasonAsync(site, staff, access);
    }

    [Fact]
    public async Task The_site_reads_the_switch_and_each_staff_member_waiting_first_and_the_managers_read_only_the_switch_and_the_counts()
    {
        var site = await SiteAsync("reads");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var approved = await JoinStaffAsync(team);
        var declined = await JoinStaffAsync(team);
        var pending = await NewSubjectAsync("pending");
        var end = Now.AddDays(3);
        (await JoinAsync(team, pending, new TeamStaffNarrowing(RoleSeeds.Viewer, true, end))).Refusal.Should().BeNull();
        (await ApproveStaffAsync(site, team, site.Owner, approved)).Should().Be(OwnerDecision.Decided);
        (await DeclineStaffAsync(site, team, site.Owner, declined)).Should().Be(OwnerDecision.Decided);

        var seen = (await Teams().ListAccessOfTenantAsync(site.Tenant, Now, Ct)).Should().ContainSingle().Subject;
        seen.TeamManagesStaff.Should().BeFalse();
        seen.Staff.Select(s => (s.SubjectId, s.Status)).Should().Equal(
            (pending, TeamSiteStaffStatus.Pending), (approved, TeamSiteStaffStatus.Approved), (declined, TeamSiteStaffStatus.Declined));
        seen.Staff[0].Narrowing.Should().Be(new TeamStaffNarrowing(RoleSeeds.Viewer, true, end));
        seen.Staff[0].HasEnded.Should().BeFalse();
        seen.Staff.Select(s => s.RoleCeilingName).Should().Equal("Viewer", null, null);

        var detail = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!;
        var managed = detail.Sites.Should().ContainSingle().Subject;
        (managed.TeamManagesStaff, managed.PendingStaffCount, managed.DeclinedStaffCount).Should().Be((false, 1, 1));
    }

    [Fact]
    public async Task The_member_history_states_each_decision_and_the_switch()
    {
        var site = await SiteAsync("history");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var staff = await JoinStaffAsync(team);
        (await DeclineStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await ApproveStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await SetStaffRuleAsync(site, team, true)).Should().Be(OwnerDecision.Decided);
        var history = new MemberHistoryReader(Contexts(site.Tenant));

        var own = await history.GetAsync(site.Tenant, staff, 25, 0, Now, Ct);
        own.Data.Select(e => e.Kind).Should().ContainInOrder(MemberHistoryKind.TeamStaffApproved, MemberHistoryKind.TeamStaffDeclined);
        own.Data.Where(e => e.Kind is MemberHistoryKind.TeamStaffApproved or MemberHistoryKind.TeamStaffDeclined)
            .Should().OnlyContain(e => e.Team!.Name == team.Name && e.Actor.SubjectId == site.Owner && e.Member!.SubjectId == staff);

        var rule = (await history.GetAsync(site.Tenant, null, 25, 0, Now, Ct)).Data
            .Should().ContainSingle(e => e.Kind == MemberHistoryKind.TeamStaffRuleChanged).Subject;
        rule.TeamManagesStaff.Should().BeTrue();
        rule.Member.Should().BeNull();
        rule.Team!.Name.Should().Be(team.Name);
    }
}
