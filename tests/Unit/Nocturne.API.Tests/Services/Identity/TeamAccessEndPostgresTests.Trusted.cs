using FluentAssertions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A site that trusts a team's whole staff (<see cref="TeamStaffRule.TeamTrusted"/>): every staff row gives the
/// team's reason within its staff member's own limits, the rows and declines are held as they were and apply
/// again in the commit that stops the trust, no owner decides about one staff member, and no notice is filed.
/// </summary>
public partial class TeamAccessEndPostgresTests
{
    private sealed record Staffed(Site Site, Team Team, Guid Access, Guid Approved, Guid Pending, Guid Declined);

    private static readonly TeamStaffNarrowing ApprovedLimits = new(null, true, null);

    private static TeamStaffNarrowing PendingLimits => new(null, false, Now.AddDays(2));

    /// <summary>A site whose owners approve each staff member, with one staff member of each status.</summary>
    private async Task<Staffed> EachStatusAsync(string slug)
    {
        var site = await SiteAsync(slug);
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole, site.CaretakerRole], staffRule: TeamStaffRule.OwnerApproves));
        var approved = await NewSubjectAsync("approved");
        (await JoinAsync(team, approved, ApprovedLimits)).Refusal.Should().BeNull();
        var pending = await NewSubjectAsync("pending");
        (await JoinAsync(team, pending, PendingLimits)).Refusal.Should().BeNull();
        var declined = await JoinStaffAsync(team);
        (await ApproveStaffAsync(site, team, site.Owner, approved)).Should().Be(OwnerDecision.Decided);
        (await DeclineStaffAsync(site, team, site.Owner, declined)).Should().Be(OwnerDecision.Decided);
        return new Staffed(site, team, access, approved, pending, declined);
    }

    private async Task<List<string>> RowsAndDeclinesAsync(Guid access) =>
        await ReadAsync($"""
            SELECT m.subject_id::text || ':' || s.status FROM team_site_staff s JOIN team_members m ON m.id = s.team_member_id
             WHERE s.team_tenant_access_id = '{access}'
            UNION ALL
            SELECT subject_id::text || ':declined-memory' FROM team_site_declines WHERE team_tenant_access_id = '{access}'
            ORDER BY 1
            """);

    private async Task<string> StoredRuleAsync(Guid access) =>
        (await ReadAsync($"SELECT staff_rule FROM team_tenant_access WHERE id = '{access}'")).Single();

    [Fact]
    public async Task Trusting_the_team_gives_every_staff_row_a_reason_within_each_staff_members_own_limits()
    {
        var staffed = await EachStatusAsync("trusted-reasons");
        var (site, team, access) = (staffed.Site, staffed.Team, staffed.Access);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staffed.Pending, Ct)).Should().BeNull();
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staffed.Declined, Ct)).Should().BeNull();

        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);

        foreach (var staff in new[] { staffed.Approved, staffed.Pending, staffed.Declined })
            await HoldsTeamReasonAsync(site, staff, access);
        (await ReasonAsync(site, staffed.Approved, access)).Should().BeEquivalentTo(
            new Held([site.ViewerRole, site.CaretakerRole], null, true, null));
        (await ReasonAsync(site, staffed.Pending, access)).Should().BeEquivalentTo(
            new Held([site.ViewerRole, site.CaretakerRole], null, false, PendingLimits.EndsAt));
        (await ReasonAsync(site, staffed.Declined, access)).Should().BeEquivalentTo(
            new Held([site.ViewerRole, site.CaretakerRole], null, false, null));
        (await StatusAsync(access, staffed.Pending)).Should().Be(TeamSiteStaffStatus.Pending);
        (await StatusAsync(access, staffed.Declined)).Should().Be(TeamSiteStaffStatus.Declined);
        (await OutOfLineAsync(staffed.Declined, site)).Should().BeEmpty();
        await ReconcileAsync();
        await HoldsTeamReasonAsync(site, staffed.Declined, access);
    }

    /// <summary>
    /// Trusts the team at <see cref="EachStatusAsync"/>'s site, then sets <paramref name="next"/> while the
    /// declined staff member's membership is held: the declined loses the reason in the commit that changes the
    /// rule, with what they made stopped. What the pending get depends on <paramref name="next"/>.
    /// </summary>
    private async Task<Staffed> StopTrustingAsync(string next)
    {
        var staffed = await EachStatusAsync($"trusted-back-{next.Replace('_', '-')}");
        var (site, team, access) = (staffed.Site, staffed.Team, staffed.Access);
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        var made = await MadeByAsync(site, staffed.Declined);
        var sessions = SessionsOf(site, made.Link, made.Credential);

        await using (var holder = Context(site.Tenant, null))
        {
            await using var held = await holder.Database.BeginTransactionAsync(Ct);
            await holder.LockMembershipAsync(site.Tenant, staffed.Declined, Ct);
            var back = Service(site.Tenant, site.Owner, sessions).SetStaffRuleAsync(site.Tenant, team.Id, site.Owner, next, Ct);
            await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, back);

            (await StoredRuleAsync(access)).Should().Be(TeamStaffRule.TeamTrusted, "nothing of the change is seen before its reasons are written");
            (await StatusAsync(access, staffed.Pending)).Should().Be(TeamSiteStaffStatus.Pending);
            await HoldsTeamReasonAsync(site, staffed.Declined, access);

            await held.RollbackAsync(Ct);
            (await back.WaitAsync(Eventually.DefaultPatience)).Should().Be(OwnerDecision.Decided);
        }

        (await StoredRuleAsync(access)).Should().Be(next);
        await HoldsTeamReasonAsync(site, staffed.Approved, access);
        (await StatusAsync(access, staffed.Declined)).Should().Be(TeamSiteStaffStatus.Declined);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staffed.Declined, Ct)).Should().BeNull();
        (await StateAsync(site, made)).Should().Be(Stopped);
        sessions.TryGet(site.Tenant, made.Link, out _).Should().BeFalse("the revoked guest link's session is evicted");
        (await RowsAsync(site.Tenant, staffed.Declined)).Should().Contain(r => r.EventType == AuthAuditEventType.MemberRemoved)
            .And.NotContain(r => r.EventType == AuthAuditEventType.TeamStaffApproved);
        return staffed;
    }

    [Fact]
    public async Task Stopping_the_trust_for_an_owner_approving_takes_the_reason_from_the_pending_and_the_declined_in_its_own_commit_and_stops_what_they_made()
    {
        var staffed = await StopTrustingAsync(TeamStaffRule.OwnerApproves);

        (await StatusAsync(staffed.Access, staffed.Pending)).Should().Be(TeamSiteStaffStatus.Pending);
        (await Members(staffed.Site.Tenant).FindMemberAsync(staffed.Site.Tenant, staffed.Pending, Ct)).Should().BeNull();
        (await RowsAsync(staffed.Site.Tenant, staffed.Pending)).Should().NotContain(r => r.EventType == AuthAuditEventType.TeamStaffApproved);
    }

    [Fact]
    public async Task Stopping_the_trust_for_the_team_deciding_approves_the_pending_and_takes_the_reason_from_the_declined_in_its_own_commit_and_stops_what_they_made()
    {
        var staffed = await StopTrustingAsync(TeamStaffRule.TeamDecides);

        (await StatusAsync(staffed.Access, staffed.Pending)).Should().Be(TeamSiteStaffStatus.Approved);
        await HoldsTeamReasonAsync(staffed.Site, staffed.Pending, staffed.Access);
        (await RowsAsync(staffed.Site.Tenant, staffed.Pending)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.TeamStaffApproved)
            .Which.Team.Should().Be(staffed.Team.Id);
    }

    [Fact]
    public async Task Trusting_the_team_changes_no_staff_row_and_no_decline_and_the_team_deciding_after_it_approves_only_the_waiting()
    {
        var staffed = await EachStatusAsync("trusted-rows");
        var before = await RowsAndDeclinesAsync(staffed.Access);
        before.Should().HaveCount(4);

        (await SetStaffRuleAsync(staffed.Site, staffed.Team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        (await RowsAndDeclinesAsync(staffed.Access)).Should().Equal(before);
        (await SetStaffRuleAsync(staffed.Site, staffed.Team, TeamStaffRule.OwnerApproves)).Should().Be(OwnerDecision.Decided);
        (await RowsAndDeclinesAsync(staffed.Access)).Should().Equal(before);
        (await SetStaffRuleAsync(staffed.Site, staffed.Team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        (await RowsAndDeclinesAsync(staffed.Access)).Should().Equal(before);

        (await SetStaffRuleAsync(staffed.Site, staffed.Team, TeamStaffRule.TeamDecides)).Should().Be(OwnerDecision.Decided);

        var waiting = $"{staffed.Pending}:{TeamSiteStaffStatus.Pending}";
        (await RowsAndDeclinesAsync(staffed.Access)).Should().BeEquivalentTo(
            before.Select(r => r == waiting ? $"{staffed.Pending}:{TeamSiteStaffStatus.Approved}" : r),
            "the team deciding approves the waiting and leaves the declined and their memory as they were");
    }

    [Fact]
    public async Task Stopping_the_trust_for_the_team_deciding_files_no_waiting_notice()
    {
        var site = await SiteAsync("trusted-decides-notices");
        var team = await TeamAsync("Clinic");
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        var waiting = await JoinStaffAsync(team, 2);
        CountOf(Waiting(site.Owner)).Should().Be(2);
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        Waiting(site.Owner).Should().BeNull();

        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamDecides)).Should().Be(OwnerDecision.Decided);

        Waiting(site.Owner).Should().BeNull("under the team deciding nobody waits");
        foreach (var staff in waiting)
        {
            (await StatusAsync(access, staff)).Should().Be(TeamSiteStaffStatus.Approved);
            await HoldsTeamReasonAsync(site, staff, access);
        }
    }

    [Fact]
    public async Task The_managers_count_the_waiting_and_the_declined_only_under_a_rule_that_keeps_them_out()
    {
        var staffed = await EachStatusAsync("trusted-counts");
        var (site, team) = (staffed.Site, staffed.Team);

        async Task<(string, int, int)> CountsAsync()
        {
            var managed = (await Teams(team.Manager).GetAsync(team.Id, team.Manager, Now, Ct))!.Sites.Should().ContainSingle().Subject;
            return (managed.StaffRule, managed.PendingStaffCount, managed.DeclinedStaffCount);
        }

        (await CountsAsync()).Should().Be((TeamStaffRule.OwnerApproves, 1, 1));
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        (await CountsAsync()).Should().Be((TeamStaffRule.TeamTrusted, 0, 0), "nobody waits and no decline applies while the site trusts the team");
        (await SetStaffRuleAsync(site, team, TeamStaffRule.OwnerApproves)).Should().Be(OwnerDecision.Decided);
        (await CountsAsync()).Should().Be((TeamStaffRule.OwnerApproves, 1, 1));
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamDecides)).Should().Be(OwnerDecision.Decided);
        (await CountsAsync()).Should().Be((TeamStaffRule.TeamDecides, 0, 1));
    }

    [Fact]
    public async Task Approving_and_declining_a_staff_member_of_a_trusted_team_are_refused_and_write_nothing()
    {
        var staffed = await EachStatusAsync("trusted-refused");
        var (site, team, access) = (staffed.Site, staffed.Team, staffed.Access);
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);
        var rows = await RowsAndDeclinesAsync(access);
        var audited = await AuditCountAsync(site);

        (await ApproveStaffAsync(site, team, site.Owner, staffed.Pending)).Should().Be(OwnerDecision.TeamTrusted);
        (await ApproveStaffAsync(site, team, site.Owner, staffed.Declined)).Should().Be(OwnerDecision.TeamTrusted);
        (await DeclineStaffAsync(site, team, site.Owner, staffed.Approved)).Should().Be(OwnerDecision.TeamTrusted);

        (await RowsAndDeclinesAsync(access)).Should().Equal(rows);
        (await AuditCountAsync(site)).Should().Be(audited);
        await HoldsTeamReasonAsync(site, staffed.Approved, access);
    }

    [Fact]
    public async Task A_new_staff_member_of_a_trusted_team_reaches_the_site_at_once_and_no_notice_is_filed()
    {
        var site = await SiteAsync("trusted-join");
        var team = await TeamAsync("Clinic");
        var early = await JoinStaffAsync(team);
        var access = await FollowAsync(team, site, Gives(site, [site.ViewerRole], staffRule: TeamStaffRule.TeamTrusted));
        await HoldsTeamReasonAsync(site, early, access);

        var joiner = await JoinStaffAsync(team);

        (await StatusAsync(access, joiner)).Should().Be(TeamSiteStaffStatus.Approved);
        await HoldsTeamReasonAsync(site, joiner, access);
        _notices.All.Should().BeEmpty("a trusted team's staff are announced neither as waiting nor as joined");
    }

    [Fact]
    public async Task Trusting_the_team_takes_back_the_waiting_and_the_joined_notices_and_stopping_the_trust_recounts()
    {
        var site = await SiteAsync("trusted-notices");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));
        await JoinStaffAsync(team);
        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamDecides)).Should().Be(OwnerDecision.Decided);
        (await SetStaffRuleAsync(site, team, TeamStaffRule.OwnerApproves)).Should().Be(OwnerDecision.Decided);
        await JoinStaffAsync(team, 2);
        LiveFor(site.Owner, InAppNotificationType.TeamStaffJoined.ToWireString()).Should().ContainSingle();
        CountOf(Waiting(site.Owner)).Should().Be(2);

        (await SetStaffRuleAsync(site, team, TeamStaffRule.TeamTrusted)).Should().Be(OwnerDecision.Decided);

        _notices.Live.Should().BeEmpty();
        _notices.All.Where(n => n.Type == InAppNotificationType.TeamStaffJoined.ToWireString())
            .Should().ContainSingle().Which.Archived.Should().Be(NotificationArchiveReason.ConditionMet);

        (await SetStaffRuleAsync(site, team, TeamStaffRule.OwnerApproves)).Should().Be(OwnerDecision.Decided);

        CountOf(Waiting(site.Owner)).Should().Be(2);
        LiveFor(site.Owner, InAppNotificationType.TeamStaffJoined.ToWireString()).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_change_of_the_rule_is_recorded_and_the_history_reads_it_and_a_row_of_the_old_switch()
    {
        var site = await SiteAsync("trusted-history");
        var team = await TeamAsync("Clinic");
        await FollowAsync(team, site, Gives(site, [site.ViewerRole], staffRule: TeamStaffRule.OwnerApproves));

        foreach (var rule in new[] { TeamStaffRule.TeamTrusted, TeamStaffRule.TeamDecides, TeamStaffRule.OwnerApproves })
            (await SetStaffRuleAsync(site, team, rule)).Should().Be(OwnerDecision.Decided);
        (await RuleRowsAsync(site)).Should().Equal(TeamStaffRule.TeamTrusted, TeamStaffRule.TeamDecides, TeamStaffRule.OwnerApproves);

        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            INSERT INTO auth_audit_log (id, event_type, tenant_id, team_id, actor_subject_id, actor_credential, success, created_at, details)
            SELECT gen_random_uuid(), event_type, tenant_id, team_id, actor_subject_id, actor_credential, success, created_at - interval '1 day' * v.days,
                   (details - '{TeamAudit.StaffRuleKey}') || jsonb_build_object('{TeamAudit.TeamManagesStaffKey}', v.manages)
              FROM auth_audit_log, (VALUES (1, 'true'::jsonb), (2, 'false'::jsonb), (3, '"yes"'::jsonb)) AS v(days, manages)
             WHERE tenant_id = '{site.Tenant}' AND event_type = '{AuthAuditEventType.TeamStaffRuleChanged}'
               AND details ->> '{TeamAudit.StaffRuleKey}' = '{TeamStaffRule.OwnerApproves}';
            """);
        var history = new MemberHistoryReader(Contexts(site.Tenant));

        var rules = (await history.GetAsync(site.Tenant, null, 25, 0, Now, Ct)).Data
            .Where(e => e.Kind == MemberHistoryKind.TeamStaffRuleChanged)
            .ToList();

        rules.Select(e => e.StaffRule).Should().Equal(
            TeamStaffRule.OwnerApproves, TeamStaffRule.TeamDecides, TeamStaffRule.TeamTrusted,
            TeamStaffRule.TeamDecides, TeamStaffRule.OwnerApproves, null);
        rules.Should().OnlyContain(e => e.Team!.Name == team.Name && e.Member == null && e.Actor.SubjectId == site.Owner);
    }
}
