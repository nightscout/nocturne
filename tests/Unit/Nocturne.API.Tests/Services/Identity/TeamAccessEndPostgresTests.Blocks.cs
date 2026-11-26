using FluentAssertions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An owner of a site stopping one person reaching it through any team (<see cref="TeamRepository.LockStaffBlockAsync"/>):
/// the stop takes every team reason at the site under each staff rule and leaves the staff rows, the declines,
/// the rule and a direct reason as they were, so removing it gives back exactly what they give.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private Task<OwnerDecision> BlockAsync(Site site, Guid caller, Guid subject, GuestSessionCacheService? sessions = null) =>
        Service(site.Tenant, caller, sessions).BlockStaffAsync(site.Tenant, caller, subject, Ct);

    private Task<OwnerDecision> UnblockAsync(Site site, Guid caller, Guid subject) =>
        Service(site.Tenant, caller).UnblockStaffAsync(site.Tenant, caller, subject, Ct);

    private async Task<int> BlocksAsync(Guid subject) =>
        int.Parse((await ReadAsync($"SELECT count(*)::text FROM team_site_blocks WHERE subject_id = '{subject}'")).Single());

    /// <summary>Every reason of the subject's membership of the site and the row itself: kind, source, roles, limit and end.</summary>
    private async Task<List<string>> MembershipOfAsync(Site site, Guid subject) =>
        await ReadAsync($"""
            SELECT r.kind || ':' || coalesce(r.source_id::text, '') || ':' || r.limit_to_24_hours::text || ':'
                   || coalesce(r.access_ends_at::text, '') || ':'
                   || coalesce((SELECT string_agg(rr.tenant_role_id::text, ',' ORDER BY rr.tenant_role_id)
                                  FROM tenant_member_reason_roles rr WHERE rr.tenant_member_reason_id = r.id), '')
              FROM tenant_member_reasons r JOIN tenant_members m ON m.id = r.tenant_member_id
             WHERE m.tenant_id = '{site.Tenant}' AND m.subject_id = '{subject}'
            UNION ALL
            SELECT 'row:' || m.limit_to_24_hours::text || ':' || coalesce(m.access_ends_at::text, '') || ':'
                   || coalesce((SELECT string_agg(mr.tenant_role_id::text, ',' ORDER BY mr.tenant_role_id)
                                  FROM tenant_member_roles mr WHERE mr.tenant_member_id = m.id), '')
              FROM tenant_members m
             WHERE m.tenant_id = '{site.Tenant}' AND m.subject_id = '{subject}'
            ORDER BY 1
            """);

    /// <summary>Two teams following a fresh site under <paramref name="rule"/>, and a staff member of both holding each one's reason there.</summary>
    private async Task<(Site Site, Team First, Guid FirstAccess, Team Second, Guid SecondAccess, Guid Staff)> StaffOfTwoTeamsAsync(
        string slug, string rule)
    {
        var site = await SiteAsync(slug);
        var first = await TeamAsync("Clinic");
        var second = await TeamAsync("Camp");
        var firstAccess = await FollowAsync(first, site, Gives(site, [site.ViewerRole], staffRule: rule));
        var secondAccess = await FollowAsync(second, site, Gives(site, [site.CaretakerRole], staffRule: rule));
        var staff = await JoinStaffAsync(first);
        await JoinStaffAsync(second, staff);
        if (rule == TeamStaffRule.OwnerApproves)
        {
            (await ApproveStaffAsync(site, first, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
            (await ApproveStaffAsync(site, second, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        }

        await HoldsTeamReasonAsync(site, staff, firstAccess);
        await HoldsTeamReasonAsync(site, staff, secondAccess);
        return (site, first, firstAccess, second, secondAccess, staff);
    }

    [Theory]
    [InlineData(TeamStaffRule.OwnerApproves)]
    [InlineData(TeamStaffRule.TeamDecides)]
    [InlineData(TeamStaffRule.TeamTrusted)]
    public async Task A_stop_takes_the_reason_of_every_team_at_the_site_ends_the_membership_with_what_they_made_and_changes_no_row(string rule)
    {
        var (site, _, firstAccess, _, secondAccess, staff) = await StaffOfTwoTeamsAsync($"block-{rule.Replace('_', '-')}", rule);
        var made = await MadeByAsync(site, staff);
        var sessions = SessionsOf(site, made.Link, made.Credential);
        var rows = (await RowsAndDeclinesAsync(firstAccess)).Concat(await RowsAndDeclinesAsync(secondAccess)).ToList();

        (await BlockAsync(site, site.Owner, staff, sessions)).Should().Be(OwnerDecision.Decided);

        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
        sessions.TryGet(site.Tenant, made.Link, out _).Should().BeFalse("the revoked guest link's session is evicted");
        (await RowsAndDeclinesAsync(firstAccess)).Concat(await RowsAndDeclinesAsync(secondAccess)).Should().Equal(rows);
        (await StoredRuleAsync(firstAccess)).Should().Be(rule);
        (await StoredRuleAsync(secondAccess)).Should().Be(rule);
        (await OutOfLineAsync(staff, site)).Should().BeEmpty();
        await ReconcileAsync();
        (await KindsAsync(site, staff)).Should().BeEmpty("the startup check reads the stop too");

        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        await HoldsTeamReasonAsync(site, staff, firstAccess);
        await HoldsTeamReasonAsync(site, staff, secondAccess);
        (await RowsAndDeclinesAsync(firstAccess)).Concat(await RowsAndDeclinesAsync(secondAccess)).Should().Equal(rows);
    }

    [Fact]
    public async Task A_stop_leaves_a_direct_membership_exactly_as_it_was_and_removing_it_adds_the_team_reason_back()
    {
        var site = await SiteAsync("block-direct");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole, site.CaretakerRole]));
        var staff = await NewSubjectAsync("staff");
        await DirectMemberAsync(site, staff, Now.AddDays(3));
        var direct = await MembershipOfAsync(site, staff);
        await JoinStaffAsync(team, staff);
        await HoldsTeamReasonAsync(site, staff, access);
        var made = await MadeByAsync(site, staff);
        var together = await MembershipOfAsync(site, staff);

        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        (await MembershipOfAsync(site, staff)).Should().Equal(direct);
        await RowIsTheSumAsync(site, staff);
        (await StateAsync(site, made)).Should().Be(CappedAt(Now.AddDays(3)), "what they made is capped at the end of the access they keep");

        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        (await MembershipOfAsync(site, staff)).Should().Equal(together);
    }

    [Theory]
    [InlineData(TeamStaffRule.OwnerApproves)]
    [InlineData(TeamStaffRule.TeamDecides)]
    [InlineData(TeamStaffRule.TeamTrusted)]
    public async Task Removing_the_stops_gives_back_exactly_what_the_rows_and_the_rule_give(string rule)
    {
        var staffed = await EachStatusAsync($"unblock-{rule.Replace('_', '-')}");
        var (site, team, access) = (staffed.Site, staffed.Team, staffed.Access);
        if (rule != TeamStaffRule.OwnerApproves)
            (await SetStaffRuleAsync(site, team, rule)).Should().Be(OwnerDecision.Decided);
        var everyone = new[] { staffed.Approved, staffed.Pending, staffed.Declined };
        var before = new List<List<string>>();
        foreach (var staff in everyone)
            before.Add(await MembershipOfAsync(site, staff));
        var rows = await RowsAndDeclinesAsync(access);
        var waiting = Waiting(site.Owner) is { } notice ? CountOf(notice) : 0;
        waiting.Should().Be(rule == TeamStaffRule.OwnerApproves ? 1 : 0);

        foreach (var staff in everyone)
            (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        foreach (var staff in everyone)
            (await KindsAsync(site, staff)).Should().BeEmpty();
        Waiting(site.Owner).Should().BeNull("no owner is told a stopped person waits");
        (await RowsAndDeclinesAsync(access)).Should().Equal(rows);

        foreach (var staff in everyone)
            (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        for (var i = 0; i < everyone.Length; i++)
            (await MembershipOfAsync(site, everyone[i])).Should().Equal(before[i]);
        (await RowsAndDeclinesAsync(access)).Should().Equal(rows);
        (Waiting(site.Owner) is { } after ? CountOf(after) : 0).Should().Be(waiting, "the pending wait again");
    }

    [Fact]
    public async Task A_stopped_person_who_joins_another_team_gets_no_reason_and_one_who_leaves_and_rejoins_stays_stopped()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("block-join");
        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var other = await TeamAsync("Camp");
        var otherAccess = await FollowAsync(other, site);
        var joinedBefore = _notices.All.Count(n => n.Type == TeamStaffAnnouncer.JoinedType);

        await JoinStaffAsync(other, staff);

        (await StatusAsync(otherAccess, staff)).Should().Be(TeamSiteStaffStatus.Approved);
        (await KindsAsync(site, staff)).Should().BeEmpty();
        _notices.All.Count(n => n.Type == TeamStaffAnnouncer.JoinedType).Should().Be(joinedBefore, "a stopped person newly reaches nothing");

        (await Service(Guid.Empty, staff).RemoveStaffAsync(team.Id, staff, staff, Ct)).Should().BeTrue();
        await JoinStaffAsync(team, staff);

        (await KindsAsync(site, staff)).Should().BeEmpty();
        (await BlocksAsync(staff)).Should().Be(1);

        (await Service(site.Tenant, site.Owner).StopAsync(site.Tenant, other.Id, Ct)).Should().BeTrue();
        await FollowAsync(other, site);
        (await KindsAsync(site, staff)).Should().BeEmpty("the stop names no team and no access");

        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var accesses = await ReadAsync($"SELECT id::text FROM team_tenant_access WHERE tenant_id = '{site.Tenant}' ORDER BY id");
        (await KindsAsync(site, staff)).Should().BeEquivalentTo(accesses.Select(a => $"{MembershipReasonKind.Team}:{a}"));
    }

    [Fact]
    public async Task The_waiting_count_leaves_out_a_stopped_person_and_counts_them_again_once_the_stop_is_removed()
    {
        var (site, team, _, staff) = await WaitingStaffAsync("block-waiting", new TeamStaffNarrowing(null, false, null));
        await JoinStaffAsync(team);
        CountOf(Waiting(site.Owner)).Should().Be(2);
        var joined = _notices.All.Count(n => n.Type == TeamStaffAnnouncer.JoinedType);

        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        CountOf(Waiting(site.Owner)).Should().Be(1);

        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        CountOf(Waiting(site.Owner)).Should().Be(2);
        _notices.All.Count(n => n.Type == TeamStaffAnnouncer.JoinedType).Should().Be(joined, "a stop files no notice of its own");
    }

    [Fact]
    public async Task The_managers_and_the_staff_member_count_the_stopping_sites_apart_from_the_approved_and_the_waiting()
    {
        var team = await TeamAsync("Clinic");
        var staff = await NewSubjectAsync("staff");
        var approved = await SiteAsync("count-approved");
        var pending = await SiteAsync("count-pending");
        var stoppedApproved = await SiteAsync("count-stopped-approved");
        var stoppedPending = await SiteAsync("count-stopped-pending");
        var stoppedTrusted = await SiteAsync("count-stopped-trusted");
        await FollowAsync(team, approved);
        await FollowAsync(team, pending, Gives(pending, [pending.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        await FollowAsync(team, stoppedApproved);
        await FollowAsync(team, stoppedPending, Gives(stoppedPending, [stoppedPending.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        await FollowAsync(team, stoppedTrusted, Gives(stoppedTrusted, [stoppedTrusted.ViewerRole], staffRule: TeamStaffRule.TeamTrusted));
        await JoinStaffAsync(team, staff);
        foreach (var site in new[] { stoppedApproved, stoppedPending, stoppedTrusted })
            (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        var member = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!.Staff.Should().ContainSingle().Subject;
        (member.SitesApproved, member.SitesPending, member.SitesBlocked).Should().Be((1, 1, 3));
        (await Teams(staff).GetAsync(team.Id, staff, Now, Ct))!.OwnStaff!.SitesBlocked.Should().Be(3);

        (await UnblockAsync(stoppedTrusted, stoppedTrusted.Owner, staff)).Should().Be(OwnerDecision.Decided);
        member = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!.Staff.Single();
        (member.SitesApproved, member.SitesPending, member.SitesBlocked).Should().Be((2, 1, 2));
        (await Teams(staff).GetAsync(team.Id, staff, Now, Ct))!.OwnStaff!.SitesBlocked.Should().Be(2);
    }

    [Fact]
    public async Task The_site_reads_each_stopped_staff_member_and_lists_its_stops_with_people_no_longer_on_a_team()
    {
        var (site, team, _, staff) = await StaffWithReasonAsync("block-list");
        var kept = await JoinStaffAsync(team);
        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        var listed = (await Teams().ListAccessOfTenantAsync(site.Tenant, Now, Ct)).Single().Staff;
        listed.Single(s => s.SubjectId == staff).Blocked.Should().BeTrue();
        listed.Single(s => s.SubjectId == kept).Blocked.Should().BeFalse();

        (await Service(Guid.Empty, team.Manager).RemoveStaffAsync(team.Id, team.Manager, staff, Ct)).Should().BeTrue();

        var block = (await Teams().ListStaffBlocksOfTenantAsync(site.Tenant, Ct)).Should().ContainSingle().Subject;
        (block.SubjectId, block.BlockedBySubjectId).Should().Be((staff, site.Owner));
        block.DisplayName.Should().StartWith("staff-");
        block.BlockedByName.Should().StartWith("block-list-owner-");
        (await Teams().ListStaffBlocksOfTenantAsync((await SiteAsync("block-list-other")).Tenant, Ct)).Should().BeEmpty();

        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await Teams().ListStaffBlocksOfTenantAsync(site.Tenant, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_an_owner_by_a_direct_reason_stops_and_a_person_who_is_not_staff_here_is_not_found()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("block-who");
        var viewer = await NewSubjectAsync("viewer");
        await DirectMemberAsync(site, viewer, endsAt: null);
        var other = await JoinStaffAsync(team);
        var stranger = await NewSubjectAsync("stranger");
        var elsewhere = await StaffWithReasonAsync("block-who-elsewhere");
        var audited = await AuditCountAsync(site);

        (await BlockAsync(site, viewer, staff)).Should().Be(OwnerDecision.NotOwner);
        (await BlockAsync(site, other, staff)).Should().Be(OwnerDecision.AccessFromTeam);
        (await UnblockAsync(site, viewer, staff)).Should().Be(OwnerDecision.NotOwner);
        (await UnblockAsync(site, other, staff)).Should().Be(OwnerDecision.AccessFromTeam);
        (await BlockAsync(site, site.Owner, stranger)).Should().Be(OwnerDecision.NotFound);
        (await BlockAsync(site, site.Owner, elsewhere.Staff)).Should().Be(OwnerDecision.NotFound, "staff of a team at another site only");
        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided, "removing a stop that is not there changes nothing");

        (await BlocksAsync(staff)).Should().Be(0);
        (await AuditCountAsync(site)).Should().Be(audited);
        await HoldsTeamReasonAsync(site, staff, access);
    }

    [Fact]
    public async Task Setting_a_stop_twice_writes_once_and_account_deletion_removes_it()
    {
        var (site, _, _, staff) = await StaffWithReasonAsync("block-twice");
        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        var audited = await AuditCountAsync(site);
        var notices = _notices.All.Count;

        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        (await AuditCountAsync(site)).Should().Be(audited);
        _notices.All.Count.Should().Be(notices);
        (await BlocksAsync(staff)).Should().Be(1);

        await Deletion(staff).DeleteAsync(staff, false, Ct);

        (await BlocksAsync(staff)).Should().Be(0);
    }

    [Fact]
    public async Task Setting_and_removing_a_stop_are_recorded_and_the_history_reads_them_for_the_site_and_the_person()
    {
        var (site, _, _, staff) = await StaffWithReasonAsync("block-history");

        (await BlockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);
        (await UnblockAsync(site, site.Owner, staff)).Should().Be(OwnerDecision.Decided);

        (await RowsAsync(site.Tenant, staff)).Where(r => r.EventType is AuthAuditEventType.TeamStaffBlocked or AuthAuditEventType.TeamStaffUnblocked)
            .Should().SatisfyRespectively(
                blocked => (blocked.EventType, blocked.Team).Should().Be((AuthAuditEventType.TeamStaffBlocked, (Guid?)null)),
                unblocked => (unblocked.EventType, unblocked.Team).Should().Be((AuthAuditEventType.TeamStaffUnblocked, (Guid?)null)));
        var history = new MemberHistoryReader(Contexts(site.Tenant));
        foreach (var narrowed in new Guid?[] { null, staff })
        {
            var entries = (await history.GetAsync(site.Tenant, narrowed, 25, 0, Now, Ct)).Data
                .Where(e => e.Kind is MemberHistoryKind.TeamStaffBlocked or MemberHistoryKind.TeamStaffUnblocked)
                .ToList();
            entries.Select(e => e.Kind).Should().Equal(MemberHistoryKind.TeamStaffUnblocked, MemberHistoryKind.TeamStaffBlocked);
            entries.Should().OnlyContain(e => e.Member!.SubjectId == staff && e.Actor.SubjectId == site.Owner && e.Team == null);
        }
    }
}
