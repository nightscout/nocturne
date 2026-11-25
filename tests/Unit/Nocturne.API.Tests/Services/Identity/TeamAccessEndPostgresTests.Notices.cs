using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The notices to a site's owners about a team's staff (<see cref="TeamStaffAnnouncer"/>) after each write has
/// committed, on PostgreSQL: one waiting notice per owner and team stating how many of the staff wait, however
/// large the team, which every write that changes that number keeps right and the last one takes back; and one
/// joined notice per owner and team when staff newly reach the site.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private IEnumerable<RecordingNotices.Notice> LiveFor(Guid user, string type) =>
        _notices.Live.Where(n => n.User == user.ToString() && n.Type == type);

    private RecordingNotices.Notice? Waiting(Guid owner) => LiveFor(owner, TeamStaffAnnouncer.WaitingType).SingleOrDefault();

    private static int CountOf(RecordingNotices.Notice? notice) => int.Parse(notice!.Metadata![TeamStaffAnnouncer.CountKey].ToString()!);

    private async Task<List<Guid>> JoinStaffAsync(Team team, int count)
    {
        var staff = new List<Guid>();
        for (var i = 0; i < count; i++)
            staff.Add(await JoinStaffAsync(team));
        return staff;
    }

    [Fact]
    public async Task A_large_team_waiting_at_a_site_gives_each_owner_one_notice_stating_the_count_that_the_decisions_bring_down()
    {
        var (site, team, _, first) = await WaitingStaffAsync("notice-count", new TeamStaffNarrowing(null, false, null));
        var more = await JoinStaffAsync(team, 10);
        var notice = Waiting(site.Owner)!;
        (notice.Tenant, notice.Source, notice.Title).Should().Be((site.Tenant, team.Id.ToString(), TeamStaffAnnouncer.WaitingTitleKey));
        CountOf(notice).Should().Be(11);
        notice.Metadata!.Keys.Should().BeEquivalentTo(TeamStaffAnnouncer.TeamKey, TeamStaffAnnouncer.TeamIdKey, TeamStaffAnnouncer.CountKey);

        foreach (var staff in more)
            (await ApproveStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        CountOf(Waiting(site.Owner)).Should().Be(1);

        (await DeclineStaffAsync(site, team, site.Owner, first)).Should().Be(OwnerDecision.Decided);
        Waiting(site.Owner).Should().BeNull();
        _notices.All.Where(n => n.Type == TeamStaffAnnouncer.WaitingType).Should().OnlyContain(n => n.Archived != null);
    }

    [Fact]
    public async Task A_widening_that_sends_an_approved_staff_member_back_to_waiting_raises_the_count()
    {
        var (site, team, _, staff) = await WaitingStaffAsync("notice-again", new TeamStaffNarrowing(RoleSeeds.Viewer, false, null));
        (await ApproveStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        Waiting(site.Owner).Should().BeNull();

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, false, null), Ct))
            .Should().Be(TeamStaffChange.Changed);

        CountOf(Waiting(site.Owner)).Should().Be(1);
    }

    [Fact]
    public async Task A_narrowing_changes_no_count()
    {
        var (_, team, _, staff) = await WaitingStaffAsync("notice-narrow", new TeamStaffNarrowing(null, false, null));
        var before = _notices.All.Count;

        await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null), Ct);

        _notices.All.Should().HaveCount(before);
    }

    [Fact]
    public async Task A_rejoin_the_decline_memory_declines_changes_no_count_and_files_no_joined_notice()
    {
        var (site, team, access, staff) = await WaitingStaffAsync("notice-declined", new TeamStaffNarrowing(null, false, null));
        (await DeclineStaffAsync(site, team, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await Service(Guid.Empty, staff).RemoveStaffAsync(team.Id, staff, staff, Ct)).Should().BeTrue();
        var before = _notices.All.Count;

        (await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, null))).Refusal.Should().BeNull();

        (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Declined);
        _notices.All.Should().HaveCount(before);
        _notices.Live.Should().BeEmpty();
    }

    [Fact]
    public async Task A_team_given_access_files_one_notice_stating_how_many_of_its_staff_wait()
    {
        var site = await SiteAsync("notice-approval");
        var team = await TeamAsync("Clinic");
        await JoinStaffAsync(team, 3);

        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));

        CountOf(Waiting(site.Owner)).Should().Be(3);
        LiveFor(site.Owner, TeamStaffAnnouncer.JoinedType).Should().BeEmpty();
    }

    [Fact]
    public async Task A_team_given_access_with_the_switch_on_files_one_joined_notice_however_many_staff_it_has()
    {
        var site = await SiteAsync("notice-approval-on");
        var team = await TeamAsync("Clinic");
        await JoinStaffAsync(team, 3);

        await FollowAsync(team, site);

        var joined = LiveFor(site.Owner, TeamStaffAnnouncer.JoinedType).Should().ContainSingle().Subject;
        (joined.Source, joined.Title).Should().Be((team.Id.ToString(), TeamStaffAnnouncer.JoinedTitleKey));
        Waiting(site.Owner).Should().BeNull();
    }

    [Fact]
    public async Task A_staff_join_where_the_team_manages_its_staff_files_one_joined_notice_and_a_second_files_none()
    {
        var site = await SiteAsync("notice-joined");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site);
        await DirectMemberAsync(site, await NewSubjectAsync("viewer"), null);

        await JoinStaffAsync(team);
        await JoinStaffAsync(team);

        _notices.Live.Should().ContainSingle().Which.Should().Match<RecordingNotices.Notice>(n =>
            n.User == site.Owner.ToString() && n.Type == TeamStaffAnnouncer.JoinedType && n.Source == team.Id.ToString());
    }

    [Fact]
    public async Task Switching_the_rule_on_takes_the_waiting_notice_back_and_files_one_joined_notice()
    {
        var (site, team, _, _) = await WaitingStaffAsync("notice-switch-on", new TeamStaffNarrowing(null, false, null));
        await JoinStaffAsync(team, 2);
        CountOf(Waiting(site.Owner)).Should().Be(3);

        await SetStaffRuleAsync(site, team, true);

        Waiting(site.Owner).Should().BeNull();
        LiveFor(site.Owner, TeamStaffAnnouncer.JoinedType).Should().ContainSingle();
    }

    [Fact]
    public async Task A_ceiling_that_gives_nothing_files_no_joined_notice()
    {
        var site = await SiteAsync("notice-ceiling");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site);

        var staff = await NewSubjectAsync("staff");
        (await JoinAsync(team, staff, new TeamStaffNarrowing("no-such-role", false, null))).Refusal.Should().BeNull();

        (await KindsAsync(site, staff)).Should().BeEmpty("an unknown ceiling gives nothing");
        _notices.Live.Should().BeEmpty();
    }

    public enum Ending
    {
        Approve,
        Decline,
        SwitchOn,
        Leave,
        Stop,
        DeleteTeam,
    }

    [Theory]
    [InlineData(Ending.Approve)]
    [InlineData(Ending.Decline)]
    [InlineData(Ending.SwitchOn)]
    [InlineData(Ending.Leave)]
    [InlineData(Ending.Stop)]
    [InlineData(Ending.DeleteTeam)]
    public async Task Every_write_that_ends_the_waiting_takes_the_waiting_notice_back(Ending ending)
    {
        var (site, team, _, staff) = await WaitingStaffAsync($"notice-end-{ending}".ToLowerInvariant(), new TeamStaffNarrowing(null, false, null));
        CountOf(Waiting(site.Owner)).Should().Be(1);

        switch (ending)
        {
            case Ending.Approve:
                await ApproveStaffAsync(site, team, site.Owner, staff);
                break;
            case Ending.Decline:
                await DeclineStaffAsync(site, team, site.Owner, staff);
                break;
            case Ending.SwitchOn:
                await SetStaffRuleAsync(site, team, true);
                break;
            case Ending.Leave:
                await Service(Guid.Empty, staff).RemoveStaffAsync(team.Id, staff, staff, Ct);
                break;
            case Ending.Stop:
                await Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct);
                break;
            case Ending.DeleteTeam:
                (await Service(Guid.Empty, team.Manager).DeleteTeamAsync(team.Id, team.Manager, Ct)).Should().Be(TeamDeletion.Deleted);
                break;
        }

        Waiting(site.Owner).Should().BeNull();
        _notices.All.Where(n => n.Type == TeamStaffAnnouncer.WaitingType).Should().OnlyContain(n => n.Archived == NotificationArchiveReason.ConditionMet);
    }

    [Fact]
    public async Task Two_owners_each_get_their_own_notice_and_a_viewer_gets_none()
    {
        var site = await SiteAsync("notice-owners");
        var team = await TeamAsync("Clinic");
        var second = await NewSubjectAsync("second-owner");
        var ownerRole = (await Members(site.Tenant).FindRoleIdBySlugAsync(site.Tenant, RoleSeeds.Owner, Ct))!.Value;
        (await Members(site.Tenant).AddMemberAsync(new NewTenantMember(site.Tenant, second, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        await DirectMemberAsync(site, await NewSubjectAsync("viewer"), null);
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));

        await JoinStaffAsync(team, 2);

        _notices.Live.Select(n => n.User).Should().BeEquivalentTo(site.Owner.ToString(), second.ToString());
        _notices.Live.Should().OnlyContain(n => CountOf(n) == 2);
    }

    private async Task AddOwnerAsync(Site site, Guid subject) =>
        (await Members(site.Tenant).AddMemberAsync(
                new NewTenantMember(site.Tenant, subject, [await OwnerRoleAsync(site)]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();

    private async Task ReplaceRoleAsync(Site site, Guid subject, Guid removed, Guid added) =>
        (await Tenants(site.Tenant).ReplaceMemberRolesAsync(site.Tenant, await MemberIdAsync(site, subject), [removed], [added], Ct))
            .Should().Be(OwnerGuardedChange.Changed);

    /// <summary>A site whose team's staff wait for its approval, and a second owner of it who is one of those staff.</summary>
    private async Task<(Site Site, Guid StaffOwner)> PendingStaffOwnerAsync(string slug)
    {
        var site = await SiteAsync(slug);
        var team = await TeamAsync("Clinic");
        var staffOwner = await NewSubjectAsync("staff-owner");
        await AddOwnerAsync(site, staffOwner);
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        await JoinStaffAsync(team, staffOwner);
        (await StatusAsync(access, staffOwner)).Should().Be(TeamSiteStaffStatus.Pending);
        Waiting(site.Owner).Should().BeNull("an owner is not counted");
        return (site, staffOwner);
    }

    [Fact]
    public async Task An_owner_who_steps_down_while_their_own_row_is_pending_raises_the_count()
    {
        var (site, stepping) = await PendingStaffOwnerAsync("notice-step-down");

        await ReplaceRoleAsync(site, stepping, await OwnerRoleAsync(site), site.ViewerRole);

        CountOf(Waiting(site.Owner)).Should().Be(1);
        LiveFor(stepping, TeamStaffAnnouncer.WaitingType).Should().BeEmpty("they are no longer an owner");
    }

    [Fact]
    public async Task Removing_an_owner_whose_own_row_is_pending_raises_the_count()
    {
        var (site, removed) = await PendingStaffOwnerAsync("notice-remove-owner");

        (await Tenants(site.Tenant).RemoveMemberAsync(site.Tenant, removed, Ct)).Ok.Should().BeTrue();

        CountOf(Waiting(site.Owner)).Should().Be(1);
    }

    /// <remarks>An owner cannot be given an end, so setting one cannot make an owner who is pending staff count.</remarks>
    [Fact]
    public async Task An_end_set_on_an_owner_whose_own_row_is_pending_is_refused_and_changes_no_count()
    {
        var (site, ending) = await PendingStaffOwnerAsync("notice-owner-end");
        var member = (await Members(site.Tenant).FindMemberAsync(site.Tenant, ending, Ct))!;

        (await Tenants(site.Tenant).SetMemberAccessEndAsync(site.Tenant, member.Id, Now.AddDays(-1), Now, Ct))
            .Should().Be(OwnerGuardedChange.OwnerAccessEnds);

        Waiting(site.Owner).Should().BeNull();
    }

    [Fact]
    public async Task A_member_made_an_owner_is_told_how_many_staff_already_wait()
    {
        var site = await SiteAsync("notice-new-owner");
        var team = await TeamAsync("Clinic");
        var promoted = await NewSubjectAsync("promoted");
        await DirectMemberAsync(site, promoted, null);
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        await JoinStaffAsync(team, 2);
        Waiting(promoted).Should().BeNull();

        await ReplaceRoleAsync(site, promoted, site.ViewerRole, await OwnerRoleAsync(site));

        CountOf(Waiting(promoted)).Should().Be(2);
    }

    [Fact]
    public async Task An_owner_who_stops_owning_the_site_has_their_waiting_notice_taken_back()
    {
        var site = await SiteAsync("notice-former-owner");
        var team = await TeamAsync("Clinic");
        var former = await NewSubjectAsync("former-owner");
        await AddOwnerAsync(site, former);
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        await JoinStaffAsync(team, 2);
        CountOf(Waiting(former)).Should().Be(2);

        await ReplaceRoleAsync(site, former, await OwnerRoleAsync(site), site.ViewerRole);

        Waiting(former).Should().BeNull();
        _notices.All.Last(n => n.User == former.ToString()).Archived.Should().Be(NotificationArchiveReason.ConditionMet);
        CountOf(Waiting(site.Owner)).Should().Be(2);
    }

    [Fact]
    public async Task Deleting_the_account_of_the_last_staff_member_who_waits_takes_the_notice_back()
    {
        var (site, _, _, staff) = await WaitingStaffAsync("notice-deleted", new TeamStaffNarrowing(null, false, null));
        CountOf(Waiting(site.Owner)).Should().Be(1);

        (await Deletion(staff).DeleteAsync(staff, false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        Waiting(site.Owner).Should().BeNull();
        _notices.All.Should().ContainSingle().Which.Archived.Should().Be(NotificationArchiveReason.ConditionMet);
    }

    [Fact]
    public async Task Staff_whose_place_or_whose_teams_access_has_ended_are_not_counted()
    {
        var (site, team, access, ended) = await WaitingStaffAsync("notice-ended", new TeamStaffNarrowing(null, false, null));
        await JoinStaffAsync(team);
        CountOf(Waiting(site.Owner)).Should().Be(2);
        var recount = new StaffNotices([new TeamSiteKey(site.Tenant, team.Id)], []);
        var past = $"{Now.AddHours(-1):yyyy-MM-dd HH:mm:ss}";

        await NpgsqlTestCommands.ExecuteAsync(
            database.MigratorConnectionString, $"UPDATE team_members SET ends_at = '{past}' WHERE team_id = '{team.Id}' AND subject_id = '{ended}'");
        await Announcer(Contexts(Guid.Empty)).AnnounceAsync(recount);
        CountOf(Waiting(site.Owner)).Should().Be(1);

        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            UPDATE team_tenant_access SET access_ends_at = '{past}' WHERE id = '{access}';
            """);
        await Announcer(Contexts(Guid.Empty)).AnnounceAsync(recount);
        Waiting(site.Owner).Should().BeNull();
    }

    [Fact]
    public async Task Narrowing_or_widening_an_approved_staff_member_where_the_team_manages_its_staff_files_no_joined_notice()
    {
        var site = await SiteAsync("notice-held");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole, site.CaretakerRole]));
        var staff = await JoinStaffAsync(team);
        LiveFor(site.Owner, TeamStaffAnnouncer.JoinedType).Should().ContainSingle();
        foreach (var notice in _notices.Live)
            notice.Archived = NotificationArchiveReason.Dismissed;

        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(RoleSeeds.Viewer, false, null), Ct))
            .Should().Be(TeamStaffChange.Changed);
        await HoldsTeamReasonAsync(site, staff, access);
        (await Service(Guid.Empty, team.Manager).UpdateStaffAsync(team.Id, team.Manager, staff, new TeamStaffNarrowing(null, false, null), Ct))
            .Should().Be(TeamStaffChange.Changed);
        await HoldsTeamReasonAsync(site, staff, access);

        _notices.Live.Should().BeEmpty("the staff member reached the site before, so no one is new");
    }

    private Task ApproveRowAsync(Site site, Guid access, Guid staff) =>
        NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{site.Tenant}', false);
            UPDATE team_site_staff SET status = '{TeamSiteStaffStatus.Approved}'
             WHERE team_tenant_access_id = '{access}'
               AND team_member_id IN (SELECT id FROM team_members WHERE subject_id = '{staff}');
            """);

    /// <remarks>
    /// The first recount is held part way through its filing, under the lock on the team at the site, and the
    /// second must wait for it. With <paramref name="laterWriteWhileHeld"/> the second write commits after the
    /// first recount has read, so the first files a count the second then replaces; without it both writes commit
    /// before either recount reads.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_recounts_of_one_team_at_once_end_with_the_count_the_last_write_left_and_stack_nothing(bool laterWriteWhileHeld)
    {
        var (site, team, access, first) = await WaitingStaffAsync(
            $"notice-race-{laterWriteWhileHeld}".ToLowerInvariant(), new TeamStaffNarrowing(null, false, null));
        var second = await JoinStaffAsync(team);
        await JoinStaffAsync(team);
        CountOf(Waiting(site.Owner)).Should().Be(3);
        var announcer = Announcer(Contexts(Guid.Empty));
        var recount = new StaffNotices([new TeamSiteKey(site.Tenant, team.Id)], []);

        await ApproveRowAsync(site, access, first);
        if (!laterWriteWhileHeld)
            await ApproveRowAsync(site, access, second);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _notices.BeforeWrite = () =>
        {
            _notices.BeforeWrite = null;
            reached.SetResult();
            return release.Task;
        };
        var ahead = announcer.AnnounceAsync(recount);
        await reached.Task.WaitAsync(Eventually.DefaultPatience);
        if (laterWriteWhileHeld)
            await ApproveRowAsync(site, access, second);

        var behind = announcer.AnnounceAsync(recount);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, behind);
        release.SetResult();
        await ahead.WaitAsync(Eventually.DefaultPatience);
        await behind.WaitAsync(Eventually.DefaultPatience);

        CountOf(LiveFor(site.Owner, TeamStaffAnnouncer.WaitingType).Should().ContainSingle().Subject).Should().Be(1);
    }

    [Fact]
    public async Task A_waiting_notice_and_a_rename_notice_for_the_same_owner_are_both_live()
    {
        var (site, team, _, _) = await WaitingStaffAsync("notice-rename", new TeamStaffNarrowing(null, false, null));
        var contexts = Contexts(Guid.Empty, team.Manager);
        await Teams(team.Manager).RenameAsync(team.Id, team.Manager, "Renamed", Ct);
        var rename = new TeamRenameAnnouncer(
            new TeamRepository(contexts),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new SiteNotices(new TenantRepository(contexts), _notices.Services()),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            NullLogger<TeamRenameAnnouncer>.Instance);

        await rename.AnnounceAsync(team.Id, team.Name);

        _notices.Live.Where(n => n.User == site.Owner.ToString()).Select(n => n.Type).Should().BeEquivalentTo(
            TeamStaffAnnouncer.WaitingType, TeamRenameAnnouncer.NotificationType);
    }

    [Fact]
    public async Task A_join_that_is_refused_announces_nothing()
    {
        var site = await SiteAsync("notice-refused");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], teamManagesStaff: false));
        var staff = await NewSubjectAsync("staff");
        var token = await StaffInviteAsync(team, new TeamStaffNarrowing(null, false, null));
        (await Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct)).Refusal.Should().BeNull();
        var before = _notices.All.Count;

        (await Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct)).Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);

        _notices.All.Should().HaveCount(before);
    }

    private TenantService Tenants(Guid tenant)
    {
        var contexts = Contexts(tenant, null);
        return new TenantService(
            contexts,
            new TenantRepository(contexts),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OperatorConfiguration()),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ITenantRoleService>(),
            new GrantRevocationService(NewSessions()),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            NullLogger<TenantService>.Instance,
            AccountLinkRoutes.Off,
            Announcer(contexts));
    }
}
