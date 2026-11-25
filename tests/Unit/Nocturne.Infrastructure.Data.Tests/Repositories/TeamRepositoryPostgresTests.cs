using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="TeamRepository"/> and the team half of <see cref="SubjectRepository.DeleteAsync"/> on
/// PostgreSQL as the runtime role, under the <c>subject_pin</c> policy and the team manager lock.
/// </summary>
[Trait("Category", "Integration")]
public class TeamRepositoryPostgresTests(TeamRepositoryPostgresTests.Database database)
    : IClassFixture<TeamRepositoryPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync() =>
            AppConnectionString = (await SharedPostgres.CreateMigratedDatabaseAsync("team_repo")).AppConnectionString;

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private static readonly DateTime Now = DateTime.UtcNow;

    private NocturneDbContext Context(IAuditContext? actor = null, bool retry = true) =>
        TestContexts.Context(database.AppConnectionString, Guid.Empty, npgsql: retry ? TestContexts.RetryingBriefly : null).ActingAs(actor);

    private SuppliedContextFactory Contexts(Guid? caller = null) =>
        new(() => Context(caller is { } id ? new TestAuditActor { SubjectId = id } : null));

    private TeamRepository Teams(Guid? caller = null) => new(Contexts(caller));

    private SubjectRepository Subjects() => new(new SuppliedContextFactory(() => Context()), AccountLinkRoutes.Off);

    private Task<Guid> NewSubjectAsync(string name = "person") =>
        Subjects().CreateAsync(new NewSubject($"{name}-{Guid.NewGuid():N}"), CancellationToken.None);

    private async Task<Guid> AddManagerAsync(Guid teamId, Guid by, Guid joiner)
    {
        var token = Guid.NewGuid().ToString("N");
        (await Teams(by).CreateInviteAsync(teamId, by, new NewTeamInvite(token, null, Now.AddDays(1), 1), default)).Should().NotBeNull();
        (await Teams(joiner).AcceptInviteAsync(token, joiner, Now, default)).Refusal.Should().BeNull();
        return joiner;
    }

    private async Task<List<AuthAuditLogEntity>> AuditOfAsync(Guid teamId)
    {
        await using var db = Context();
        return await db.AuthAuditLog.AsNoTracking().Where(a => a.TeamId == teamId).ToListAsync();
    }

    private static Dictionary<string, string?> Details(AuthAuditLogEntity row) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(row.DetailsJson!)!;

    [Fact]
    public async Task Creating_a_team_makes_the_creator_its_one_manager_and_audits_both()
    {
        var creator = await NewSubjectAsync();

        var team = await Teams(creator).CreateAsync(creator, "Camp staff", default);

        var detail = await Teams().GetAsync(team.Id, creator, Now, default);
        detail!.Name.Should().Be("Camp staff");
        detail.Managers.Select(m => m.SubjectId).Should().Equal(creator);
        detail.IsLastManager.Should().BeTrue();
        detail.CanDelete.Should().BeTrue();
        detail.CallerKind.Should().Be(TeamMemberKind.Manager);

        var rows = await AuditOfAsync(team.Id);
        rows.Select(r => r.EventType).Should().BeEquivalentTo([AuthAuditEventType.TeamCreated, AuthAuditEventType.TeamManagerAdded]);
        rows.Should().OnlyContain(r => r.TenantId == null && r.ActorSubjectId == creator);
        var created = rows.Single(r => r.EventType == AuthAuditEventType.TeamCreated);
        created.TargetSubjectId.Should().BeNull();
        Details(created).Should().Equal(new Dictionary<string, string?> { ["name"] = "Camp staff", ["kind"] = "team" });
        var added = rows.Single(r => r.EventType == AuthAuditEventType.TeamManagerAdded);
        added.TargetSubjectId.Should().Be(creator);
        Details(added)["kind"].Should().Be("team");
    }

    [Fact]
    public async Task My_teams_lists_only_the_teams_the_subject_is_a_member_of()
    {
        var a = await NewSubjectAsync();
        var b = await NewSubjectAsync();
        var mine = await Teams(a).CreateAsync(a, "B team", default);
        var shared = await Teams(a).CreateAsync(a, "A team", default);
        await Teams(b).CreateAsync(b, "Not mine", default);
        await AddManagerAsync(shared.Id, a, b);

        (await Teams().GetTeamsOfAsync(a, default)).Should().Equal(
            new TeamSummary(shared.Id, "A team", TeamMemberKind.Manager), new TeamSummary(mine.Id, "B team", TeamMemberKind.Manager));
    }

    [Fact]
    public async Task A_non_member_reads_and_changes_nothing()
    {
        var manager = await NewSubjectAsync();
        var stranger = await NewSubjectAsync();
        var team = await Teams(manager).CreateAsync(manager, "Family", default);
        var token = Guid.NewGuid().ToString("N");
        var invite = await Teams(manager).CreateInviteAsync(team.Id, manager, new NewTeamInvite(token, null, Now.AddDays(1), null), default);
        var auditBefore = (await AuditOfAsync(team.Id)).Count;
        var teams = Teams(stranger);

        (await teams.GetAsync(team.Id, stranger, Now, default)).Should().BeNull();
        (await teams.RenameAsync(team.Id, stranger, "Mine now", default)).Should().BeNull();
        (await teams.RemoveManagerAsync(team.Id, stranger, manager, Now, default)).Should().Be(ManagerGuardedChange.NotFound);
        (await teams.CreateInviteAsync(team.Id, stranger, new NewTeamInvite("x", null, Now.AddDays(1), null), default)).Should().BeNull();
        (await teams.RevokeInviteAsync(team.Id, stranger, invite!.Value, Now, default)).Should().BeFalse();
        (await TeamWrites.DeleteAsync(Contexts(stranger), team.Id, stranger)).Should().Be(TeamDeletion.NotFound);

        var detail = await Teams().GetAsync(team.Id, manager, Now, default);
        detail!.Name.Should().Be("Family");
        detail.Managers.Should().ContainSingle();
        detail.Invites.Should().ContainSingle();
        (await AuditOfAsync(team.Id)).Should().HaveCount(auditBefore);
    }

    [Fact]
    public async Task Renaming_audits_the_old_and_the_new_name()
    {
        var manager = await NewSubjectAsync();
        var team = await Teams(manager).CreateAsync(manager, "Old", default);

        (await Teams(manager).RenameAsync(team.Id, manager, "New", default)).Should().Be("Old");

        (await Teams().GetAsync(team.Id, manager, Now, default))!.Name.Should().Be("New");
        var row = (await AuditOfAsync(team.Id)).Single(r => r.EventType == AuthAuditEventType.TeamRenamed);
        row.ActorSubjectId.Should().Be(manager);
        Details(row).Should().Equal(new Dictionary<string, string?> { ["old_name"] = "Old", ["name"] = "New", ["kind"] = "team" });
    }

    [Fact]
    public async Task An_invite_adds_the_person_who_opens_it_once_and_is_audited()
    {
        var manager = await NewSubjectAsync("manager");
        var joiner = await NewSubjectAsync();
        var team = await Teams(manager).CreateAsync(manager, "Camp", default);
        var token = Guid.NewGuid().ToString("N");

        var inviteId = await Teams(manager).CreateInviteAsync(team.Id, manager, new NewTeamInvite(token, "Night staff", Now.AddDays(3), 5), default);

        var info = await Teams().GetInviteInfoAsync(token, joiner, Now, default);
        info.Should().BeEquivalentTo(new { TeamId = team.Id, TeamName = "Camp", Refusal = (TeamInviteRefusal?)null });
        info!.CreatedByName.Should().StartWith("manager-");
        info.Kind.Should().Be(TeamMemberKind.Manager);

        (await Teams(joiner).AcceptInviteAsync(token, joiner, Now, default)).Should().Be(new TeamInviteAcceptance(team.Id, null));
        (await Teams(joiner).AcceptInviteAsync(token, joiner, Now, default)).Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);
        (await Teams().GetInviteInfoAsync(token, joiner, Now, default))!.Refusal.Should().Be(TeamInviteRefusal.AlreadyMember);

        var detail = await Teams().GetAsync(team.Id, joiner, Now, default);
        detail!.Managers.Select(m => m.SubjectId).Should().Equal(manager, joiner);
        detail.Invites.Should().ContainSingle().Which.Should().Be(
            new TeamManagerInvite(inviteId!.Value, "Night staff", detail.Invites[0].ExpiresAt, 5, 1, manager, detail.Managers[0].DisplayName));

        var rows = await AuditOfAsync(team.Id);
        var created = rows.Single(r => r.EventType == AuthAuditEventType.TeamInviteCreated);
        created.ActorSubjectId.Should().Be(manager);
        Details(created)["invite"].Should().Be(inviteId.ToString());
        var added = rows.Single(r => r.EventType == AuthAuditEventType.TeamManagerAdded && r.TargetSubjectId == joiner);
        added.TargetSubjectId.Should().Be(joiner);
        added.ActorSubjectId.Should().Be(joiner);
        Details(added)["invite"].Should().Be(inviteId.ToString());
    }

    [Fact]
    public async Task An_expired_a_revoked_and_a_used_up_invite_each_state_why_and_add_no_one()
    {
        var manager = await NewSubjectAsync();
        var joiner = await NewSubjectAsync();
        var second = await NewSubjectAsync();
        var team = await Teams(manager).CreateInviteTeamAsync(manager);
        var expired = Guid.NewGuid().ToString("N");
        var revoked = Guid.NewGuid().ToString("N");
        var single = Guid.NewGuid().ToString("N");
        await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(expired, null, Now.AddMinutes(-1), null), default);
        var revokedId = await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(revoked, null, Now.AddDays(1), null), default);
        await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(single, null, Now.AddDays(1), 1), default);
        (await Teams(manager).RevokeInviteAsync(team, manager, revokedId!.Value, Now, default)).Should().BeTrue();
        (await Teams(joiner).AcceptInviteAsync(single, joiner, Now, default)).Refusal.Should().BeNull();

        (await Teams(second).AcceptInviteAsync(expired, second, Now, default)).Refusal.Should().Be(TeamInviteRefusal.Expired);
        (await Teams(second).AcceptInviteAsync(revoked, second, Now, default)).Refusal.Should().Be(TeamInviteRefusal.Revoked);
        (await Teams(second).AcceptInviteAsync(single, second, Now, default)).Refusal.Should().Be(TeamInviteRefusal.UsedUp);
        (await Teams(second).AcceptInviteAsync("no-such-token", second, Now, default)).Refusal.Should().Be(TeamInviteRefusal.NotFound);

        (await Teams().GetTeamsOfAsync(second, default)).Should().BeEmpty();
        (await Teams().GetAsync(team, manager, Now, default))!.Invites.Should().BeEmpty("none of the three can be accepted");
        var revokedRow = (await AuditOfAsync(team)).Single(r => r.EventType == AuthAuditEventType.TeamInviteRevoked);
        Details(revokedRow)["invite"].Should().Be(revokedId.ToString());
    }

    [Fact]
    public async Task A_manager_removes_another_and_leaves_until_they_are_the_last()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var b = await AddManagerAsync(team, a, await NewSubjectAsync());
        var c = await AddManagerAsync(team, a, await NewSubjectAsync());

        (await Teams(a).RemoveManagerAsync(team, a, b, Now, default)).Should().Be(ManagerGuardedChange.Changed);
        (await Teams(c).RemoveManagerAsync(team, c, c, Now, default)).Should().Be(ManagerGuardedChange.Changed);
        (await Teams(a).RemoveManagerAsync(team, a, a, Now, default)).Should().Be(ManagerGuardedChange.LastManager);
        (await Teams(a).RemoveManagerAsync(team, a, b, Now, default)).Should().Be(ManagerGuardedChange.NotFound);

        (await Teams().GetAsync(team, a, Now, default))!.Managers.Select(m => m.SubjectId).Should().Equal(a);
        (await Teams().GetAsync(team, b, Now, default)).Should().BeNull();
        var removed = (await AuditOfAsync(team)).Where(r => r.EventType == AuthAuditEventType.TeamManagerRemoved).ToList();
        removed.Select(r => (r.TargetSubjectId, r.ActorSubjectId)).Should().BeEquivalentTo([((Guid?)b, (Guid?)a), (c, c)]);
    }

    [Fact]
    public async Task A_manager_who_cannot_sign_in_does_not_count()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var b = await AddManagerAsync(team, a, await NewSubjectAsync());
        (await Subjects().UpdateAsync(b, new SubjectEdit(IsActive: false), default)).Should().BeOfType<SubjectChange.Changed>();

        (await Teams().GetAsync(team, a, Now, default))!.IsLastManager.Should().BeTrue();
        (await Teams(a).RemoveManagerAsync(team, a, a, Now, default)).Should().Be(ManagerGuardedChange.LastManager);
        (await Teams(a).RemoveManagerAsync(team, a, b, Now, default)).Should().Be(ManagerGuardedChange.Changed);
    }

    [Fact]
    public async Task Two_managers_leaving_at_once_leaves_one()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var b = await AddManagerAsync(team, a, await NewSubjectAsync());

        await using var holder = Context(retry: false);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockTeamManagersAsync(team, default);

        var leaveA = Teams(a).RemoveManagerAsync(team, a, a, Now, default);
        var leaveB = Teams(b).RemoveManagerAsync(team, b, b, Now, default);
        await UntilWaitingAsync(2, leaveA, leaveB);
        await held.RollbackAsync();

        var outcomes = await Task.WhenAll(leaveA, leaveB).WaitAsync(Eventually.DefaultPatience);
        outcomes.Should().BeEquivalentTo([ManagerGuardedChange.Changed, ManagerGuardedChange.LastManager]);
        var left = outcomes[0] == ManagerGuardedChange.Changed ? b : a;
        (await Teams().GetAsync(team, left, Now, default))!.Managers.Select(m => m.SubjectId).Should().Equal(left);
    }

    [Fact]
    public async Task Deleting_a_team_removes_it_with_its_managers_and_invites_and_is_audited()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var b = await AddManagerAsync(team, a, await NewSubjectAsync());

        (await TeamWrites.DeleteAsync(Contexts(b), team, b)).Should().Be(TeamDeletion.Deleted);

        (await Teams().GetTeamsOfAsync(a, default)).Should().BeEmpty();
        await using (var db = Context())
        {
            (await db.TeamMembers.AnyAsync(m => m.TeamId == team)).Should().BeFalse();
            (await db.TeamInvites.AnyAsync(i => i.TeamId == team)).Should().BeFalse();
        }

        var row = (await AuditOfAsync(team)).Single(r => r.EventType == AuthAuditEventType.TeamDeleted);
        row.ActorSubjectId.Should().Be(b);
        Details(row).Should().Equal(new Dictionary<string, string?> { ["name"] = "Invite team", ["kind"] = "team" });
    }

    [Fact]
    public async Task Deleting_the_only_member_of_a_team_deletes_the_team_and_audits_it()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);

        (await Subjects().DeleteAsync(a, default)).Should().BeOfType<SubjectChange.Changed>();

        await using (var db = Context())
            (await db.Teams.AnyAsync(t => t.Id == team)).Should().BeFalse();
        var row = (await AuditOfAsync(team)).Single(r => r.EventType == AuthAuditEventType.TeamDeleted);
        row.TeamId.Should().Be(team);
        Details(row)["name"].Should().Be("Invite team");
    }

    [Fact]
    public async Task Deleting_the_last_manager_of_a_team_others_belong_to_is_refused_naming_the_team()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var b = await AddManagerAsync(team, a, await NewSubjectAsync());
        (await Subjects().UpdateAsync(b, new SubjectEdit(IsActive: false), default)).Should().BeOfType<SubjectChange.Changed>(
            "deactivating a manager is allowed and leaves the team as it is");

        var refused = await Subjects().DeleteAsync(a, default);

        refused.Should().BeEquivalentTo(new SubjectChange.LastManagerOf([new LastManagerTeam(team, "Invite team")]));
        (await Subjects().GetAsync(a, default)).Should().NotBeNull();
        (await Teams().GetAsync(team, a, Now, default))!.Managers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Deleting_a_manager_another_counting_manager_stays_with_keeps_the_team()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var b = await AddManagerAsync(team, a, await NewSubjectAsync());

        (await Subjects().DeleteAsync(b, default)).Should().BeOfType<SubjectChange.Changed>();

        (await Teams().GetAsync(team, a, Now, default))!.Managers.Select(m => m.SubjectId).Should().Equal(a);
        (await AuditOfAsync(team)).Should().NotContain(r => r.EventType == AuthAuditEventType.TeamDeleted);
    }

    [Fact]
    public async Task A_removed_manager_cannot_accept_their_own_invite_and_nor_can_anyone_else()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var r = await AddManagerAsync(team, a, await NewSubjectAsync());
        var newcomer = await NewSubjectAsync();
        var token = Guid.NewGuid().ToString("N");
        var invite = await Teams(r).CreateInviteAsync(team, r, new NewTeamInvite(token, null, Now.AddDays(90), null), default);

        (await Teams(a).RemoveManagerAsync(team, a, r, Now, default)).Should().Be(ManagerGuardedChange.Changed);

        (await Teams(r).AcceptInviteAsync(token, r, Now, default)).Refusal.Should().Be(TeamInviteRefusal.Revoked);
        (await Teams(newcomer).AcceptInviteAsync(token, newcomer, Now, default)).Refusal.Should().Be(TeamInviteRefusal.Revoked);
        (await Teams().GetAsync(team, a, Now, default))!.Managers.Select(m => m.SubjectId).Should().Equal(a);
        var revoked = (await AuditOfAsync(team)).Single(row => row.EventType == AuthAuditEventType.TeamInviteRevoked);
        revoked.ActorSubjectId.Should().Be(a);
        Details(revoked)["invite"].Should().Be(invite.ToString());
    }

    [Fact]
    public async Task An_invite_whose_maker_no_longer_manages_the_team_is_refused_even_if_not_revoked()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var r = await AddManagerAsync(team, a, await NewSubjectAsync());
        var newcomer = await NewSubjectAsync();
        var token = Guid.NewGuid().ToString("N");
        await Teams(r).CreateInviteAsync(team, r, new NewTeamInvite(token, null, Now.AddDays(1), null), default);
        await using (var db = Context())
            await db.Database.ExecuteSqlAsync($"DELETE FROM team_members WHERE team_id = {team} AND subject_id = {r}");

        (await Teams().GetInviteInfoAsync(token, newcomer, Now, default))!.Refusal.Should().Be(TeamInviteRefusal.NoLongerValid);
        (await Teams(r).AcceptInviteAsync(token, r, Now, default)).Refusal.Should().Be(TeamInviteRefusal.NoLongerValid);
        (await Teams(newcomer).AcceptInviteAsync(token, newcomer, Now, default)).Refusal.Should().Be(TeamInviteRefusal.NoLongerValid);
        (await Teams().GetAsync(team, a, Now, default))!.Invites.Should().BeEmpty();
    }

    [Fact]
    public async Task A_manager_who_leaves_loses_their_invites_and_a_staying_managers_invite_is_unaffected()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var r = await AddManagerAsync(team, a, await NewSubjectAsync());
        var newcomer = await NewSubjectAsync();
        var theirs = Guid.NewGuid().ToString("N");
        var kept = Guid.NewGuid().ToString("N");
        await Teams(r).CreateInviteAsync(team, r, new NewTeamInvite(theirs, null, Now.AddDays(1), null), default);
        await Teams(r).CreateInviteAsync(team, r, new NewTeamInvite(Guid.NewGuid().ToString("N"), null, Now.AddDays(1), null), default);
        var keptId = await Teams(a).CreateInviteAsync(team, a, new NewTeamInvite(kept, "Stays", Now.AddDays(1), null), default);

        (await Teams(r).RemoveManagerAsync(team, r, r, Now, default)).Should().Be(ManagerGuardedChange.Changed);

        var detail = await Teams().GetAsync(team, a, Now, default);
        detail!.Invites.Select(i => (i.Id, i.CreatedBySubjectId)).Should().Equal((keptId!.Value, a));
        (await AuditOfAsync(team)).Count(row => row.EventType == AuthAuditEventType.TeamInviteRevoked && row.ActorSubjectId == r)
            .Should().Be(2);
        (await Teams(newcomer).AcceptInviteAsync(theirs, newcomer, Now, default)).Refusal.Should().Be(TeamInviteRefusal.Revoked);
        (await Teams(newcomer).AcceptInviteAsync(kept, newcomer, Now, default)).Refusal.Should().BeNull();
    }

    [Fact]
    public async Task An_invite_made_while_the_makers_removal_holds_the_lock_is_refused_once_the_removal_commits()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var r = await AddManagerAsync(team, a, await NewSubjectAsync());

        await using var removal = Context(retry: false);
        await using var held = await removal.Database.BeginTransactionAsync();
        await removal.LockTeamManagersAsync(team, default);
        await removal.Database.ExecuteSqlAsync($"DELETE FROM team_members WHERE team_id = {team} AND subject_id = {r}");

        var create = Teams(r).CreateInviteAsync(team, r, new NewTeamInvite(Guid.NewGuid().ToString("N"), null, Now.AddDays(1), null), default);
        await UntilWaitingAsync(1, create);
        await held.CommitAsync();

        (await create.WaitAsync(Eventually.DefaultPatience)).Should().BeNull("the check runs after the removal commits");
        await using var db = Context();
        (await db.TeamInvites.AnyAsync(i => i.TeamId == team && i.CreatedBySubjectId == r)).Should().BeFalse();
    }

    [Fact]
    public async Task A_member_who_is_not_a_manager_reads_the_team_without_its_invites_and_runs_nothing()
    {
        var a = await NewSubjectAsync();
        var team = await Teams(a).CreateInviteTeamAsync(a);
        var staff = await NewSubjectAsync();
        var token = Guid.NewGuid().ToString("N");
        var invite = await Teams(a).CreateInviteAsync(team, a, new NewTeamInvite(token, null, Now.AddDays(1), null), default);
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO team_members (id, team_id, subject_id, kind) VALUES ({Guid.CreateVersion7()}, {team}, {staff}, 'staff')");
        }

        var teams = Teams(staff);

        var detail = await teams.GetAsync(team, staff, Now, default);
        detail.Should().NotBeNull();
        detail!.Managers.Should().BeEmpty("a staff member reads no manager's subject id");
        detail.ManagerNames.Should().HaveCount(1);
        detail.Invites.Should().BeEmpty();
        detail.CanDelete.Should().BeFalse();
        detail.IsLastManager.Should().BeFalse();
        (await teams.RenameAsync(team, staff, "Taken", default)).Should().BeNull();
        (await teams.CreateInviteAsync(team, staff, new NewTeamInvite("x", null, Now.AddDays(1), null), default)).Should().BeNull();
        (await teams.RevokeInviteAsync(team, staff, invite!.Value, Now, default)).Should().BeFalse();
        (await teams.RemoveManagerAsync(team, staff, a, Now, default)).Should().Be(ManagerGuardedChange.NotFound);
        (await TeamWrites.DeleteAsync(Contexts(staff), team, staff)).Should().Be(TeamDeletion.Refused);

        var forManager = await Teams().GetAsync(team, a, Now, default);
        forManager!.Name.Should().Be("Invite team");
        forManager.Invites.Should().ContainSingle().Which.CreatedByName.Should().Be(forManager.Managers[0].DisplayName);
    }

    private Task UntilWaitingAsync(int sessions, params Task[] contenders) =>
        PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, sessions, contenders);
}

internal static class TeamRepositoryTestExtensions
{
    public static async Task<Guid> CreateInviteTeamAsync(this TeamRepository teams, Guid creator) =>
        (await teams.CreateAsync(creator, "Invite team", default)).Id;
}
