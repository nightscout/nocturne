using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// A team's access to sites through <see cref="TeamRepository"/> on PostgreSQL as the runtime role:
/// the links, the approval under the team manager and team access locks, the site's list and stop,
/// the <c>tenant_pin</c> policy, the role edits it guards, team deletion and the member history.
/// </summary>
[Trait("Category", "Integration")]
public class TeamAccessPostgresTests(TeamAccessPostgresTests.Database database)
    : IClassFixture<TeamAccessPostgresTests.Database>
{
    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var db = await SharedPostgres.CreateMigratedDatabaseAsync("team_access");
            AppConnectionString = db.AppConnectionString;
            MigratorConnectionString = db.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Site(Guid Id, string Name, IReadOnlyDictionary<string, Guid> Roles);

    /// <summary>
    /// Now, cut to whole microseconds as <c>timestamp with time zone</c> stores it, so an instant the
    /// test writes and the one it then compares are the same instant.
    /// </summary>
    private static readonly DateTime Now = WholeMicroseconds(DateTime.UtcNow);

    private static DateTime WholeMicroseconds(DateTime instant) =>
        new(instant.Ticks - instant.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

    private NocturneDbContext Context(IAuditContext? actor = null, Guid? tenant = null, bool retry = true) =>
        TestContexts.Context(
            database.AppConnectionString, tenant ?? Guid.Empty, npgsql: retry ? TestContexts.RetryingBriefly : null).ActingAs(actor);

    private SuppliedContextFactory Contexts(Guid? actor = null, Guid? tenant = null) =>
        new(() => Context(actor is { } id ? new TestAuditActor { SubjectId = id } : null, tenant));

    private TeamRepository Teams(Guid? actor = null) => new(Contexts(actor));

    private TenantMembershipRepository Members(Guid? actor = null) => new(Contexts(actor), AccountLinkRoutes.Off);

    private Task<Guid> NewSubjectAsync(string name = "person") =>
        new SubjectRepository(Contexts(), AccountLinkRoutes.Off).CreateAsync(new NewSubject($"{name}-{Guid.NewGuid():N}"), default);

    private async Task<Site> NewSiteAsync(string name = "Site")
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, @name, true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"team-access-{tenant:N}"));
            cmd.Parameters.Add(new NpgsqlParameter("@name", name));
            await cmd.ExecuteNonQueryAsync();
        }

        await Members().SeedRolesAsync(tenant, default);
        var roles = (await Members().ListRolesAsync(tenant, default)).ToDictionary(r => r.Slug, r => r.Id);
        return new Site(tenant, name, roles);
    }

    private async Task<Guid> RoleAsync(Site site, params string[] permissions)
    {
        var slug = $"custom-{Guid.NewGuid():N}";
        return (await Members().CreateRoleAsync(new NewTenantRole(site.Id, slug, slug, null, permissions, false), default)).Id;
    }

    private async Task<(Guid Team, Guid Manager)> TeamAsync(string name = "Diabetes clinic")
    {
        var manager = await NewSubjectAsync("manager");
        return ((await Teams(manager).CreateAsync(manager, name, default)).Id, manager);
    }

    private async Task<(string Token, Guid LinkId)> LinkAsync(
        Guid team, Guid manager, string roleSlug = RoleSeeds.Caretaker, int? maxUses = null,
        DateTime? expiresAt = null, bool limit = false, DateTime? end = null)
    {
        var token = Guid.NewGuid().ToString("N");
        var id = await Teams(manager).CreateAccessLinkAsync(team, manager, new NewTeamAccessLink(
            token, null, expiresAt ?? Now.AddDays(1), maxUses, new TeamAccessSuggestion(roleSlug, limit, end)), default);
        return (token, id!.Value);
    }

    private static TeamAccessGrant Grant(Site site, params Guid[] roles) => new(site.Id, roles, false, null, true);

    /// <summary>Approves as <paramref name="approver"/>, made an owner of the grant's site first if they are not one.</summary>
    private async Task<TeamAccessApproval> ApproveAsync(string token, Guid approver, TeamAccessGrant grant, bool limited = false)
    {
        await OwnerOfAsync(grant.TenantId, approver);
        return await TeamWrites.ApproveAccessAsync(Contexts(approver), token, Credential(approver, limited), grant, Now);
    }

    private static TeamAccessApproverCredential Credential(Guid subject, bool limited = false) =>
        new(subject, AuthType.SessionCookie, new HashSet<string>(), limited);

    private async Task OwnerOfAsync(Guid tenant, Guid subject)
    {
        if (await Members().FindMemberAsync(tenant, subject, default) is not null)
            return;
        var owner = (await Members().FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, default))!.Value;
        (await Members().AddMemberAsync(
            new NewTenantMember(tenant, subject, [owner]) { How = MembershipJoinHow.Direct, By = null }, default)).Should().BeTrue();
    }

    private async Task<List<AuthAuditLogEntity>> AuditOfAsync(Guid teamId)
    {
        await using var db = Context();
        return await db.AuthAuditLog.AsNoTracking().Where(a => a.TeamId == teamId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync();
    }

    private static JsonElement Details(AuthAuditLogEntity row) => JsonDocument.Parse(row.DetailsJson!).RootElement;

    private async Task<TeamAccessLinkEntity> StoredLinkAsync(Guid linkId)
    {
        await using var db = Context();
        return await db.TeamAccessLinks.AsNoTracking().SingleAsync(l => l.Id == linkId);
    }

    private async Task<int> AccessCountAsync(Guid team)
    {
        await using var db = Context();
        return await db.TeamTenantAccess.CountAsync(a => a.TeamId == team);
    }

    [Fact]
    public async Task A_manager_makes_and_revokes_an_access_link_and_both_are_audited()
    {
        var (team, manager) = await TeamAsync();
        var stranger = await NewSubjectAsync();
        var end = Now.AddDays(30);

        var (_, linkId) = await LinkAsync(team, manager, RoleSeeds.Viewer, maxUses: 3, limit: true, end: end);

        var link = (await Teams().GetAsync(team, manager, Now, default))!.AccessLinks.Should().ContainSingle().Subject;
        link.Id.Should().Be(linkId);
        link.MaxUses.Should().Be(3);
        link.Suggestion.RoleSlug.Should().Be(RoleSeeds.Viewer);
        link.Suggestion.LimitTo24Hours.Should().BeTrue();
        link.Suggestion.AccessEndsAt.Should().BeCloseTo(end, TimeSpan.FromMilliseconds(1));
        (await Teams(stranger).CreateAccessLinkAsync(team, stranger, new NewTeamAccessLink(
            "x", null, Now.AddDays(1), null, new TeamAccessSuggestion(RoleSeeds.Viewer, false, null)), default)).Should().BeNull();
        (await Teams(stranger).RevokeAccessLinkAsync(team, stranger, linkId, Now, default)).Should().BeFalse();

        (await Teams(manager).RevokeAccessLinkAsync(team, manager, linkId, Now, default)).Should().BeTrue();
        (await Teams(manager).RevokeAccessLinkAsync(team, manager, linkId, Now, default)).Should().BeTrue();

        (await Teams().GetAsync(team, manager, Now, default))!.AccessLinks.Should().BeEmpty();
        var rows = await AuditOfAsync(team);
        rows.Where(r => r.EventType is AuthAuditEventType.TeamAccessLinkCreated or AuthAuditEventType.TeamAccessLinkRevoked)
            .Select(r => (r.EventType, r.TenantId, r.ActorSubjectId, Link: Details(r).GetProperty("link").GetString()))
            .Should().Equal(
                (AuthAuditEventType.TeamAccessLinkCreated, (Guid?)null, (Guid?)manager, linkId.ToString()),
                (AuthAuditEventType.TeamAccessLinkRevoked, null, manager, linkId.ToString()));
    }

    [Fact]
    public async Task A_manager_reads_the_roles_a_link_may_suggest_and_a_person_outside_the_team_reads_no_team()
    {
        var (team, manager) = await TeamAsync();
        var stranger = await NewSubjectAsync();

        var roles = (await Teams().GetAsync(team, manager, Now, default))!.SuggestableRoles;

        roles.Select(r => r.Slug).Should().BeEquivalentTo(
            [RoleSeeds.Caretaker, RoleSeeds.Clinician, RoleSeeds.Viewer], o => o.WithoutStrictOrdering());
        roles.Should().OnlyContain(r => r.Permissions.Count > 0 && Scope.IsDataOnly(r.Permissions));
        roles.Single(r => r.Slug == RoleSeeds.Viewer).Permissions.Should().Equal(RoleSeeds.Permissions[RoleSeeds.Viewer]);
        (await Teams().GetAsync(team, stranger, Now, default)).Should().BeNull();
    }

    [Fact]
    public async Task An_expired_a_revoked_and_a_used_up_link_each_state_why_and_give_nothing()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var other = await NewSiteAsync();
        var (expired, _) = await LinkAsync(team, manager, expiresAt: Now.AddMinutes(-1));
        var (revoked, revokedId) = await LinkAsync(team, manager);
        var (single, _) = await LinkAsync(team, manager, maxUses: 1);
        await Teams(manager).RevokeAccessLinkAsync(team, manager, revokedId, Now, default);
        (await ApproveAsync(single, owner, Grant(other, other.Roles[RoleSeeds.Caretaker]))).Refusal.Should().BeNull();

        var grant = Grant(site, site.Roles[RoleSeeds.Caretaker]);
        (await ApproveAsync(expired, owner, grant)).Refusal.Should().Be(TeamAccessRefusal.Expired);
        (await ApproveAsync(revoked, owner, grant)).Refusal.Should().Be(TeamAccessRefusal.Revoked);
        (await ApproveAsync(single, owner, grant)).Refusal.Should().Be(TeamAccessRefusal.UsedUp);
        (await ApproveAsync("no-such-token", owner, grant)).Refusal.Should().Be(TeamAccessRefusal.NotFound);
        (await Teams().GetAccessLinkInfoAsync(expired, Now, default))!.Refusal.Should().Be(TeamAccessRefusal.Expired);
        (await Teams().GetAccessLinkInfoAsync(revoked, Now, default))!.Refusal.Should().Be(TeamAccessRefusal.Revoked);
        (await Teams().GetAccessLinkInfoAsync(single, Now, default))!.Refusal.Should().Be(TeamAccessRefusal.UsedUp);
        (await Teams().GetAccessLinkInfoAsync("no-such-token", Now, default)).Should().BeNull();

        (await Teams().ListSitesWithAccessAsync(team, [site.Id, other.Id], default)).Should().BeEquivalentTo([other.Id]);
    }

    [Fact]
    public async Task A_link_dies_with_its_makers_place_in_the_team()
    {
        var (team, a) = await TeamAsync();
        var r = await NewSubjectAsync();
        var inviteToken = Guid.NewGuid().ToString("N");
        await Teams(a).CreateInviteAsync(team, a, new NewTeamInvite(inviteToken, null, Now.AddDays(1), 1), default);
        await Teams(r).AcceptInviteAsync(inviteToken, r, Now, default);
        var (removedMakers, removedId) = await LinkAsync(team, r);
        var (deletedMakers, _) = await LinkAsync(team, a);
        var (kept, _) = await LinkAsync(team, a);
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();

        (await Teams(a).RemoveManagerAsync(team, a, r, Now, default)).Should().Be(ManagerGuardedChange.Changed);

        (await ApproveAsync(removedMakers, owner, Grant(site, site.Roles[RoleSeeds.Caretaker]))).Refusal
            .Should().Be(TeamAccessRefusal.Revoked);
        (await StoredLinkAsync(removedId)).RevokedAt.Should().NotBeNull();
        var revoked = (await AuditOfAsync(team)).Single(row => row.EventType == AuthAuditEventType.TeamAccessLinkRevoked);
        revoked.ActorSubjectId.Should().Be(a);
        Details(revoked).GetProperty("link").GetString().Should().Be(removedId.ToString());
        (await Teams().GetAsync(team, a, Now, default))!.AccessLinks.Should().HaveCount(2);

        await using (var db = Context())
            await db.Database.ExecuteSqlAsync($"DELETE FROM team_members WHERE team_id = {team} AND subject_id = {a}");
        (await Teams().GetAccessLinkInfoAsync(deletedMakers, Now, default))!.Refusal.Should().Be(TeamAccessRefusal.NoLongerValid);
        (await ApproveAsync(kept, owner, Grant(site, site.Roles[RoleSeeds.Caretaker]))).Refusal
            .Should().Be(TeamAccessRefusal.NoLongerValid);
        (await AccessCountAsync(team)).Should().Be(0);
    }

    [Fact]
    public async Task An_access_link_is_not_a_manager_invite_and_a_manager_invite_is_not_an_access_link()
    {
        var (team, manager) = await TeamAsync();
        var person = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (accessToken, _) = await LinkAsync(team, manager);
        var inviteToken = Guid.NewGuid().ToString("N");
        await Teams(manager).CreateInviteAsync(team, manager, new NewTeamInvite(inviteToken, null, Now.AddDays(1), null), default);

        (await Teams(person).AcceptInviteAsync(accessToken, person, Now, default)).Refusal.Should().Be(TeamInviteRefusal.NotFound);
        (await Teams().GetInviteInfoAsync(accessToken, person, Now, default)).Should().BeNull();
        (await ApproveAsync(inviteToken, person, Grant(site, site.Roles[RoleSeeds.Caretaker]))).Refusal
            .Should().Be(TeamAccessRefusal.NotFound);
        (await Teams().GetAccessLinkInfoAsync(inviteToken, Now, default)).Should().BeNull();
    }

    [Fact]
    public async Task Approving_writes_exactly_the_access_its_roles_the_use_and_one_audit_row()
    {
        var (team, manager) = await TeamAsync("Night clinic");
        var owner = await NewSubjectAsync("owner");
        var site = await NewSiteAsync("Sam's site");
        var end = Now.AddDays(10);
        var (token, linkId) = await LinkAsync(team, manager, RoleSeeds.Caretaker, maxUses: 2);
        var viewerish = await RoleAsync(site, Scope.GlucoseRead);

        var approval = await ApproveAsync(
            token, owner, new TeamAccessGrant(site.Id, [site.Roles[RoleSeeds.Caretaker], viewerish], true, end, true));

        approval.Refusal.Should().BeNull();
        await using (var db = Context())
        {
            var access = await db.TeamTenantAccess.AsNoTracking().Include(a => a.AccessRoles).SingleAsync(a => a.TeamId == team);
            access.Id.Should().Be(approval.AccessId!.Value);
            access.TenantId.Should().Be(site.Id);
            access.LimitTo24Hours.Should().BeTrue();
            access.AccessEndsAt.Should().BeCloseTo(end, TimeSpan.FromMilliseconds(1));
            access.ApprovedBySubjectId.Should().Be(owner);
            access.CreatedFromLinkId.Should().Be(linkId);
            access.AccessRoles.Select(r => r.TenantRoleId).Should().BeEquivalentTo([site.Roles[RoleSeeds.Caretaker], viewerish]);
            (await db.TenantMembers.Where(m => m.TenantId == site.Id).Select(m => m.SubjectId).ToListAsync())
                .Should().Equal([owner], "no staff member gains anything yet; the owner is the only member");
        }

        (await StoredLinkAsync(linkId)).UseCount.Should().Be(1);
        var given = (await AuditOfAsync(team)).Single(r => r.EventType == AuthAuditEventType.TeamAccessGiven);
        given.TenantId.Should().Be(site.Id);
        given.TargetSubjectId.Should().BeNull();
        given.ActorSubjectId.Should().Be(owner);
        var details = Details(given);
        details.GetProperty("kind").GetString().Should().Be("team_access");
        details.GetProperty("name").GetString().Should().Be("Night clinic");
        details.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).Should()
            .BeEquivalentTo([RoleSeeds.Caretaker, (await Members().GetRoleAsync(site.Id, viewerish, default))!.Slug]);
        details.GetProperty("history_limit").GetString().Should().Be("24_hours");
        details.GetProperty("access_ends_at").GetDateTime().Should().BeCloseTo(end, TimeSpan.FromMilliseconds(1));
        details.GetProperty("link").GetString().Should().Be(linkId.ToString());

        var sites = (await Teams().GetAsync(team, manager, Now, default))!.Sites;
        sites.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            TenantId = site.Id,
            DisplayName = "Sam's site",
            LimitTo24Hours = true,
        });
        sites[0].RoleNames.Should().Contain("Caretaker").And.HaveCount(2);
        sites[0].HasEnded.Should().BeFalse();
        (await Teams().GetAsync(team, manager, end.AddMinutes(1), default))!.Sites.Single().HasEnded.Should().BeTrue();

        var listed = (await Teams().ListAccessOfTenantAsync(site.Id, Now, default)).Should().ContainSingle().Subject;
        listed.TeamId.Should().Be(team);
        listed.TeamName.Should().Be("Night clinic");
        listed.ApprovedBySubjectId.Should().Be(owner);
        listed.ApprovedByName.Should().StartWith("owner-");
        listed.HasEnded.Should().BeFalse();
        (await Teams().ListAccessApproversAsync(team, default)).Should().Equal(new TeamAccessApprover(site.Id, owner, "Night clinic"));
        (await Teams().ListRolesHeldByTeamsAsync(site.Id, default)).Should().BeEquivalentTo([site.Roles[RoleSeeds.Caretaker], viewerish]);
    }

    [Fact]
    public async Task Approving_a_site_the_team_already_has_is_refused_and_takes_no_use()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, linkId) = await LinkAsync(team, manager, maxUses: 5);
        (await ApproveAsync(token, owner, Grant(site, site.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();

        (await ApproveAsync(token, owner, Grant(site, site.Roles[RoleSeeds.Caretaker]))).Refusal
            .Should().Be(TeamAccessRefusal.AlreadyGiven);

        (await StoredLinkAsync(linkId)).UseCount.Should().Be(1);
        (await AuditOfAsync(team)).Count(r => r.EventType == AuthAuditEventType.TeamAccessGiven).Should().Be(1);
    }

    [Fact]
    public async Task A_grant_with_no_role_or_a_role_of_another_site_is_refused()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var other = await NewSiteAsync();
        var (token, linkId) = await LinkAsync(team, manager);

        (await ApproveAsync(token, owner, Grant(site))).Refusal.Should().Be(TeamAccessRefusal.NoRoles);
        (await ApproveAsync(token, owner, Grant(site, other.Roles[RoleSeeds.Viewer]))).Refusal
            .Should().Be(TeamAccessRefusal.RoleNotInTenant);
        (await ApproveAsync(token, owner, Grant(site, site.Roles[RoleSeeds.Viewer], Guid.NewGuid()))).Refusal
            .Should().Be(TeamAccessRefusal.RoleNotInTenant);

        (await StoredLinkAsync(linkId)).UseCount.Should().Be(0);
        (await AccessCountAsync(team)).Should().Be(0);
    }

    public static TheoryData<string> AdministrativePermissions => new(Scope.AdministrativePermissions);

    [Theory]
    [MemberData(nameof(AdministrativePermissions))]
    public async Task A_role_that_holds_an_administrative_permission_is_refused(string permission)
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        var role = await RoleAsync(site, Scope.GlucoseRead, permission);

        (await ApproveAsync(token, owner, Grant(site, role))).Refusal.Should().Be(TeamAccessRefusal.RoleAdministrative);
        (await AccessCountAsync(team)).Should().Be(0);
    }

    [Theory]
    [InlineData(RoleSeeds.Caretaker, new[] { Scope.GlucoseRead, Scope.TreatmentsReadWrite, Scope.AlertsReadWrite }, null)]
    [InlineData(RoleSeeds.Caretaker, new[] { Scope.GlucoseRead, Scope.ConnectorsReadWrite }, null)]
    [InlineData(RoleSeeds.Caretaker, new[] { Scope.GlucoseReadWrite }, TeamAccessRefusal.RoleWiderThanLink)]
    [InlineData(RoleSeeds.Clinician, new[] { Scope.GlucoseRead, Scope.ConnectorsReadWrite }, TeamAccessRefusal.RoleWiderThanLink)]
    [InlineData(RoleSeeds.Viewer, new[] { Scope.GlucoseRead, Scope.ReportsRead }, null)]
    [InlineData(RoleSeeds.Viewer, new[] { Scope.GlucoseRead, Scope.HeartRateRead }, TeamAccessRefusal.RoleWiderThanLink)]
    [InlineData(RoleSeeds.Clinician, new[] { Scope.TreatmentsRead }, null)]
    [InlineData(RoleSeeds.Clinician, new[] { Scope.TreatmentsReadWrite }, TeamAccessRefusal.RoleWiderThanLink)]
    public async Task Every_permission_of_a_chosen_role_must_be_within_the_links_suggested_role(
        string suggested, string[] permissions, TeamAccessRefusal? refusal)
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager, suggested);
        var role = await RoleAsync(site, permissions);

        (await ApproveAsync(token, owner, Grant(site, role))).Refusal.Should().Be(refusal);
    }

    [Fact]
    public async Task A_link_that_suggests_caretaker_gives_the_sites_caretaker_which_sets_up_data_sources()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager, RoleSeeds.Caretaker);
        var caretaker = site.Roles[RoleSeeds.Caretaker];
        (await Members().GetRoleAsync(site.Id, caretaker, default))!.Permissions.Should().Contain(Scope.ConnectorsReadWrite);

        (await ApproveAsync(token, owner, Grant(site, caretaker))).Refusal.Should().BeNull();

        (await AccessCountAsync(team)).Should().Be(1);
    }

    [Fact]
    public async Task Two_approvals_of_a_single_use_link_at_once_give_one_site()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var first = await NewSiteAsync();
        var second = await NewSiteAsync();
        var (token, linkId) = await LinkAsync(team, manager, maxUses: 1);

        await using var holder = Context(retry: false);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockTeamManagersAsync(team, default);

        var a = ApproveAsync(token, owner, Grant(first, first.Roles[RoleSeeds.Caretaker]));
        var b = ApproveAsync(token, owner, Grant(second, second.Roles[RoleSeeds.Caretaker]));
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 2, a, b);
        await held.RollbackAsync();

        var outcomes = await Task.WhenAll(a, b).WaitAsync(Eventually.DefaultPatience);
        outcomes.Select(o => o.Refusal).Should().BeEquivalentTo(new TeamAccessRefusal?[] { null, TeamAccessRefusal.UsedUp });
        (await AccessCountAsync(team)).Should().Be(1);
        (await StoredLinkAsync(linkId)).UseCount.Should().Be(1);
    }

    [Fact]
    public async Task An_approval_waiting_on_a_role_edit_that_makes_the_role_administrative_reads_the_edit()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        var role = await RoleAsync(site, Scope.GlucoseRead);

        await using var edit = Context(tenant: site.Id, retry: false);
        await using var held = await edit.Database.BeginTransactionAsync();
        await edit.LockTeamAccessAsync(site.Id, default);
        var administrative = JsonSerializer.Serialize(new[] { Scope.GlucoseRead, Scope.MembersManage });
        await edit.Database.ExecuteSqlAsync(
            $"UPDATE tenant_roles SET permissions = {administrative}::jsonb WHERE id = {role}");

        var approval = ApproveAsync(token, owner, Grant(site, role));
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, approval);
        await held.CommitAsync();

        (await approval.WaitAsync(Eventually.DefaultPatience)).Refusal.Should().Be(TeamAccessRefusal.RoleAdministrative);
        (await AccessCountAsync(team)).Should().Be(0);
    }

    [Fact]
    public async Task A_role_edit_waiting_on_an_approval_reads_the_access_and_is_refused()
    {
        var (team, _) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var role = await RoleAsync(site, Scope.GlucoseRead);
        var slug = (await Members().GetRoleAsync(site.Id, role, default))!.Slug;
        var accessId = Guid.CreateVersion7();

        await using var approval = Context(retry: false);
        await using var held = await approval.Database.BeginTransactionAsync();
        await approval.LockTeamAccessAsync(site.Id, default);
        await approval.Database.ExecuteSqlAsync($"""
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours, approved_by_subject_id)
            VALUES ({accessId}, {team}, {site.Id}, false, {owner})
            """);
        await approval.Database.ExecuteSqlAsync($"""
            INSERT INTO team_tenant_access_roles (id, team_tenant_access_id, tenant_role_id)
            VALUES ({Guid.CreateVersion7()}, {accessId}, {role})
            """);

        var editing = TeamWrites.EditRoleAsync(
            Contexts(), site.Id, role, new TenantRoleEdit(slug, slug, null, [Scope.GlucoseRead, Scope.RolesManage]), null, Now);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, editing);
        await held.CommitAsync();

        (await editing.WaitAsync(Eventually.DefaultPatience)).Should().Be(new TenantRoleUpdate(RoleChangeOutcome.HeldByTeam, null));
        (await Members().GetRoleAsync(site.Id, role, default))!.Permissions.Should().Equal(Scope.GlucoseRead);
    }

    [Fact]
    public async Task A_role_a_team_holds_keeps_to_data_and_cannot_be_deleted_until_the_access_stops()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        var role = await RoleAsync(site, Scope.GlucoseRead);
        var slug = (await Members().GetRoleAsync(site.Id, role, default))!.Slug;
        (await ApproveAsync(token, owner, Grant(site, role))).Refusal.Should().BeNull();

        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, new TenantRoleEdit(slug, slug, null, [Scope.GlucoseRead, Scope.AuditRead]), owner, Now))
            .Outcome.Should().Be(RoleChangeOutcome.HeldByTeam, "not even an owner may give a team a role that runs the site");
        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, new TenantRoleEdit(slug, slug, null, [Scope.GlucoseRead, Scope.TreatmentsRead]), owner, Now))
            .Outcome.Should().Be(RoleChangeOutcome.Changed);
        (await Members().DeleteRoleAsync(site.Id, role, Now, default)).Should().Be(RoleChangeOutcome.HeldByTeam);
        (await Members().GetRoleAsync(site.Id, role, default)).Should().NotBeNull();

        (await TeamWrites.StopAccessAsync(Contexts(owner), site.Id, team)).Should().BeTrue();

        (await Members().DeleteRoleAsync(site.Id, role, Now, default)).Should().Be(RoleChangeOutcome.Changed);
    }

    [Fact]
    public async Task An_owner_may_give_a_role_a_team_holds_connector_setup()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        var role = await RoleAsync(site, Scope.GlucoseRead);
        var slug = (await Members().GetRoleAsync(site.Id, role, default))!.Slug;
        (await ApproveAsync(token, owner, Grant(site, role))).Refusal.Should().BeNull();

        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, new TenantRoleEdit(slug, slug, null, [Scope.GlucoseRead, Scope.ConnectorsReadWrite]), owner, Now))
            .Outcome.Should().Be(RoleChangeOutcome.Changed, "setting up data sources does not run the site");
        (await Members().GetRoleAsync(site.Id, role, default))!.Permissions.Should().Equal(Scope.GlucoseRead, Scope.ConnectorsReadWrite);
    }

    [Fact]
    public async Task Only_an_owner_in_force_may_widen_a_role_a_team_holds_and_anyone_who_edits_roles_may_narrow_it()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var admin = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        var role = await RoleAsync(site, Scope.GlucoseRead, Scope.TreatmentsRead);
        var slug = (await Members().GetRoleAsync(site.Id, role, default))!.Slug;
        (await ApproveAsync(token, owner, Grant(site, role))).Refusal.Should().BeNull();
        (await Members().AddMemberAsync(new NewTenantMember(site.Id, admin, [site.Roles[RoleSeeds.Admin]])
            { How = MembershipJoinHow.Direct, By = null }, default)).Should().BeTrue();
        TenantRoleEdit Edit(params string[] permissions) => new(slug, slug, null, permissions);

        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, Edit(Scope.GlucoseRead, Scope.TreatmentsReadWrite), admin, Now))
            .Outcome.Should().Be(RoleChangeOutcome.HeldByTeamOwnerOnly);
        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, Edit(Scope.GlucoseRead, Scope.TreatmentsRead, Scope.HeartRateRead), null, Now))
            .Outcome.Should().Be(RoleChangeOutcome.HeldByTeamOwnerOnly);
        (await Members().GetRoleAsync(site.Id, role, default))!.Permissions.Should().Equal(Scope.GlucoseRead, Scope.TreatmentsRead);

        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, Edit(Scope.GlucoseRead), admin, Now))
            .Outcome.Should().Be(RoleChangeOutcome.Changed, "removing a permission gives the team less");
        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, Edit(Scope.GlucoseRead, Scope.TreatmentsReadWrite), owner, Now))
            .Outcome.Should().Be(RoleChangeOutcome.Changed);

        await using (var db = Context())
            await db.Database.ExecuteSqlAsync(
                $"UPDATE tenant_members SET access_ends_at = {Now.AddMinutes(-1)} WHERE tenant_id = {site.Id} AND subject_id = {owner}");
        (await TeamWrites.EditRoleAsync(Contexts(), site.Id, role, Edit(Scope.GlucoseRead, Scope.TreatmentsReadWrite, Scope.FoodRead), owner, Now))
            .Outcome.Should().Be(RoleChangeOutcome.HeldByTeamOwnerOnly, "an owner whose access has ended is no owner in force");
    }

    [Fact]
    public async Task A_member_who_is_not_an_owner_is_refused_by_the_approval_itself_and_nothing_is_written()
    {
        var (team, manager) = await TeamAsync();
        var member = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, linkId) = await LinkAsync(team, manager);
        (await Members().AddMemberAsync(new NewTenantMember(site.Id, member, [site.Roles[RoleSeeds.Admin]])
            { How = MembershipJoinHow.Direct, By = null }, default)).Should().BeTrue();

        (await TeamWrites.ApproveAccessAsync(Contexts(member), token, Credential(member), Grant(site, site.Roles[RoleSeeds.Viewer]), Now))
            .Refusal.Should().Be(TeamAccessRefusal.NotOwner);
        (await TeamWrites.ApproveAccessAsync(Contexts(member), token, Credential(Guid.NewGuid()), Grant(site, site.Roles[RoleSeeds.Viewer]), Now))
            .Refusal.Should().Be(TeamAccessRefusal.NotOwner);

        (await AccessCountAsync(team)).Should().Be(0);
        (await StoredLinkAsync(linkId)).UseCount.Should().Be(0);
    }

    [Fact]
    public async Task An_approver_demoted_while_the_approval_waits_on_the_owners_lock_gives_nothing()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, linkId) = await LinkAsync(team, manager);
        await OwnerOfAsync(site.Id, owner);
        var membership = (await Members().FindMemberAsync(site.Id, owner, default))!.Id;

        await using var demotion = Context(retry: false);
        await using var held = await demotion.Database.BeginTransactionAsync();
        await demotion.LockTenantOwnersAsync(site.Id, default);
        await demotion.Database.ExecuteSqlAsync($"""
            UPDATE tenant_member_roles SET tenant_role_id = {site.Roles[RoleSeeds.Viewer]}
             WHERE tenant_member_id = {membership} AND tenant_role_id = {site.Roles[RoleSeeds.Owner]}
            """);

        var approval = TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), Grant(site, site.Roles[RoleSeeds.Viewer]), Now);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, approval);
        await held.CommitAsync();

        (await approval.WaitAsync(Eventually.DefaultPatience)).Refusal.Should().Be(TeamAccessRefusal.NotOwner);
        (await AccessCountAsync(team)).Should().Be(0);
        (await StoredLinkAsync(linkId)).UseCount.Should().Be(0);
        (await AuditOfAsync(team)).Should().NotContain(r => r.EventType == AuthAuditEventType.TeamAccessGiven);
        await using var db = Context();
        (await db.TeamTenantAccessRoles.AnyAsync(r => r.TenantRoleId == site.Roles[RoleSeeds.Viewer])).Should().BeFalse();
    }

    [Fact]
    public async Task The_approval_holds_the_approver_to_the_end_of_their_own_access_and_to_their_credentials_limit()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager, maxUses: 5);
        await OwnerOfAsync(site.Id, owner);
        var ownEnd = Now.AddDays(3);
        await using (var db = Context())
            await db.Database.ExecuteSqlAsync(
                $"UPDATE tenant_members SET access_ends_at = {ownEnd} WHERE tenant_id = {site.Id} AND subject_id = {owner}");
        var viewer = site.Roles[RoleSeeds.Viewer];

        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), new TeamAccessGrant(site.Id, [viewer], false, null, true), Now))
            .Refusal.Should().Be(TeamAccessRefusal.EndOutlastsApprover);
        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), new TeamAccessGrant(site.Id, [viewer], false, ownEnd.AddSeconds(1), true), Now))
            .Refusal.Should().Be(TeamAccessRefusal.EndOutlastsApprover);
        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner, limited: true), new TeamAccessGrant(site.Id, [viewer], false, ownEnd, true), Now))
            .Refusal.Should().Be(TeamAccessRefusal.LimitRequired);
        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner, limited: true), new TeamAccessGrant(site.Id, [viewer], true, ownEnd, true), Now))
            .Refusal.Should().BeNull();
    }

    private async Task AsMigratorAsync(Guid tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await conn.PinTenantAsync(tenant);
        await conn.ExecuteAsync(sql);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_approval_judges_a_direct_only_approver_by_the_row_as_it_always_has(bool keepsTheDirectReason)
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager, maxUses: 5);
        await OwnerOfAsync(site.Id, owner);
        var rowEnd = Now.AddDays(3);
        await AsMigratorAsync(site.Id, $"""
            UPDATE tenant_members SET access_ends_at = '{rowEnd:O}' WHERE tenant_id = '{site.Id}' AND subject_id = '{owner}';
            {(keepsTheDirectReason ? "" : $"DELETE FROM tenant_member_reasons r USING tenant_members m WHERE r.tenant_member_id = m.id AND m.tenant_id = '{site.Id}' AND m.subject_id = '{owner}';")}
            """);
        var viewer = site.Roles[RoleSeeds.Viewer];

        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), new TeamAccessGrant(site.Id, [viewer], false, null, true), Now))
            .Refusal.Should().Be(TeamAccessRefusal.EndOutlastsApprover, "the row's end holds the approver, whatever its direct reason says");
        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), new TeamAccessGrant(site.Id, [viewer], false, rowEnd, true), Now))
            .Refusal.Should().BeNull();
    }

    [Fact]
    public async Task The_approval_holds_an_owner_to_their_direct_reason_and_ignores_a_team_reason_beside_it()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager, maxUses: 5);
        await OwnerOfAsync(site.Id, owner);
        var directEnd = Now.AddDays(3);
        var (teamAccessSql, teamAccess) = TeamReasonSql.NewAccess(site.Id, "Clinic");
        var reason = Guid.CreateVersion7();
        await AsMigratorAsync(site.Id, $"""
            UPDATE tenant_member_reasons r SET access_ends_at = '{directEnd:O}'
              FROM tenant_members m WHERE r.tenant_member_id = m.id AND m.tenant_id = '{site.Id}' AND m.subject_id = '{owner}';
            {teamAccessSql}
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, access_ends_at)
            SELECT '{reason}', m.id, 'team', '{teamAccess}', false, NULL
              FROM tenant_members m WHERE m.tenant_id = '{site.Id}' AND m.subject_id = '{owner}';
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            VALUES ('{Guid.CreateVersion7()}', '{reason}', '{site.Roles[RoleSeeds.Caretaker]}');
            """);
        var viewer = site.Roles[RoleSeeds.Viewer];

        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), new TeamAccessGrant(site.Id, [viewer], false, null, true), Now))
            .Refusal.Should().Be(TeamAccessRefusal.EndOutlastsApprover, "the row has no end, but the owner's direct reason ends");
        (await TeamWrites.ApproveAccessAsync(Contexts(owner), token, Credential(owner), new TeamAccessGrant(site.Id, [viewer], false, directEnd, true), Now))
            .Refusal.Should().BeNull();
    }

    [Fact]
    public async Task A_link_whose_suggested_end_has_passed_or_whose_role_is_gone_cannot_be_used()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var (passed, passedId) = await LinkAsync(team, manager, end: Now.AddMinutes(-1));
        var (retired, retiredId) = await LinkAsync(team, manager);
        await using (var db = Context())
            await db.Database.ExecuteSqlAsync($"UPDATE team_access_links SET suggested_role_slug = 'retired' WHERE id = {retiredId}");
        var grant = new TeamAccessGrant(site.Id, [site.Roles[RoleSeeds.Viewer]], false, Now.AddMinutes(-2), true);

        (await Teams().GetAccessLinkInfoAsync(passed, Now, default))!.Refusal.Should().Be(TeamAccessRefusal.SuggestedEndPassed);
        (await ApproveAsync(passed, owner, grant)).Refusal.Should().Be(TeamAccessRefusal.SuggestedEndPassed);
        (await Teams().GetAccessLinkInfoAsync(retired, Now, default))!.Refusal.Should().Be(TeamAccessRefusal.NoLongerValid);
        (await ApproveAsync(retired, owner, Grant(site, site.Roles[RoleSeeds.Viewer]))).Refusal.Should().Be(TeamAccessRefusal.NoLongerValid);
        (await Teams().GetAsync(team, manager, Now, default))!.AccessLinks.Select(l => l.Id).Should().NotContain(passedId);
        (await AccessCountAsync(team)).Should().Be(0);
    }

    [Fact]
    public async Task A_team_deletion_waiting_on_a_stop_records_only_the_sites_it_still_had()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var a = await NewSiteAsync();
        var b = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        (await ApproveAsync(token, owner, Grant(a, a.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();
        (await ApproveAsync(token, owner, Grant(b, b.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();

        await using var stop = Context(tenant: a.Id, retry: false);
        await using var held = await stop.Database.BeginTransactionAsync();
        await stop.LockTeamAccessAsync(a.Id, default);
        await stop.Database.ExecuteSqlAsync($"DELETE FROM team_tenant_access WHERE team_id = {team} AND tenant_id = {a.Id}");

        var deletion = TeamWrites.DeleteAsync(Contexts(manager), team, manager);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, deletion);
        await held.CommitAsync();

        (await deletion.WaitAsync(Eventually.DefaultPatience)).Should().Be(TeamDeletion.Deleted);
        (await AuditOfAsync(team)).Where(r => r.EventType == AuthAuditEventType.TeamAccessStopped).Select(r => r.TenantId)
            .Should().Equal(b.Id);
    }

    [Fact]
    public async Task The_tenant_pin_policy_shows_a_pinned_context_only_its_sites_access_and_an_unpinned_one_all()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var a = await NewSiteAsync();
        var b = await NewSiteAsync();
        var c = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        (await ApproveAsync(token, owner, Grant(a, a.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();
        (await ApproveAsync(token, owner, Grant(b, b.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();

        Guid[] roles = [a.Roles[RoleSeeds.Viewer], b.Roles[RoleSeeds.Viewer]];

        async Task<(List<Guid> Access, List<Guid> Roles)> SeenAsync(Guid? tenant)
        {
            await using var db = Context(tenant: tenant);
            return (
                await db.TeamTenantAccess.Where(x => x.TeamId == team).Select(x => x.TenantId).ToListAsync(),
                await db.TeamTenantAccessRoles.Where(x => roles.Contains(x.TenantRoleId)).Select(x => x.TenantRoleId).ToListAsync());
        }

        (await SeenAsync(a.Id)).Should().BeEquivalentTo((new List<Guid> { a.Id }, new List<Guid> { a.Roles[RoleSeeds.Viewer] }));
        (await SeenAsync(c.Id)).Should().BeEquivalentTo((new List<Guid>(), new List<Guid>()));
        (await SeenAsync(null)).Should().BeEquivalentTo((
            new List<Guid> { a.Id, b.Id }, new List<Guid> { a.Roles[RoleSeeds.Viewer], b.Roles[RoleSeeds.Viewer] }));

        await using var pinnedToC = Context(tenant: c.Id);
        var write = () => pinnedToC.Database.ExecuteSqlAsync($"""
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours) VALUES ({Guid.CreateVersion7()}, {team}, {a.Id}, false)
            """);
        await write.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task The_site_stops_a_teams_access_at_once_and_it_is_audited()
    {
        var (team, manager) = await TeamAsync("Camp staff");
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var other = await NewSiteAsync();
        var (token, linkId) = await LinkAsync(team, manager);
        (await ApproveAsync(token, owner, Grant(site, site.Roles[RoleSeeds.Caretaker]))).Refusal.Should().BeNull();

        (await TeamWrites.StopAccessAsync(Contexts(owner), other.Id, team)).Should().BeFalse();
        (await TeamWrites.StopAccessAsync(Contexts(owner), site.Id, team)).Should().BeTrue();
        (await TeamWrites.StopAccessAsync(Contexts(owner), site.Id, team)).Should().BeFalse();

        (await AccessCountAsync(team)).Should().Be(0);
        await using (var db = Context())
            (await db.TeamTenantAccessRoles.AnyAsync(r => r.TenantRoleId == site.Roles[RoleSeeds.Caretaker])).Should().BeFalse();
        var stopped = (await AuditOfAsync(team)).Single(r => r.EventType == AuthAuditEventType.TeamAccessStopped);
        stopped.TenantId.Should().Be(site.Id);
        stopped.ActorSubjectId.Should().Be(owner);
        stopped.TargetSubjectId.Should().BeNull();
        var details = Details(stopped);
        details.GetProperty("how").GetString().Should().Be("stopped");
        details.GetProperty("name").GetString().Should().Be("Camp staff");
        details.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).Should().Equal(RoleSeeds.Caretaker);
        details.GetProperty("link").GetString().Should().Be(linkId.ToString());
        (await Teams().GetAsync(team, manager, Now, default))!.Sites.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_team_records_each_sites_access_as_stopped_before_the_cascade()
    {
        var (team, manager) = await TeamAsync("Ward 4");
        var owner = await NewSubjectAsync();
        var a = await NewSiteAsync();
        var b = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        (await ApproveAsync(token, owner, Grant(a, a.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();
        (await ApproveAsync(token, owner, Grant(b, b.Roles[RoleSeeds.Caretaker]))).Refusal.Should().BeNull();

        (await TeamWrites.DeleteAsync(Contexts(manager), team, manager)).Should().Be(TeamDeletion.Deleted);

        (await AccessCountAsync(team)).Should().Be(0);
        var stopped = (await AuditOfAsync(team)).Where(r => r.EventType == AuthAuditEventType.TeamAccessStopped).ToList();
        stopped.Select(r => (r.TenantId, How: Details(r).GetProperty("how").GetString(), Name: Details(r).GetProperty("name").GetString()))
            .Should().BeEquivalentTo([((Guid?)a.Id, "team_deleted", "Ward 4"), (b.Id, "team_deleted", "Ward 4")]);
        stopped.Single(r => r.TenantId == b.Id).Should().Match<AuthAuditLogEntity>(r =>
            Details(r).GetProperty("roles")[0].GetString() == RoleSeeds.Caretaker);
    }

    [Fact]
    public async Task Deleting_the_last_member_of_a_team_on_a_sites_host_records_every_sites_access_as_stopped()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync();
        var a = await NewSiteAsync();
        var b = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager);
        (await ApproveAsync(token, owner, Grant(a, a.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();
        (await ApproveAsync(token, owner, Grant(b, b.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();
        var onSiteA = new SubjectRepository(new SuppliedContextFactory(() => Context(tenant: a.Id)), AccountLinkRoutes.Off);

        (await onSiteA.DeleteAsync(manager, default)).Should().BeOfType<SubjectChange.Changed>();

        (await AccessCountAsync(team)).Should().Be(0);
        (await AuditOfAsync(team)).Where(r => r.EventType == AuthAuditEventType.TeamAccessStopped).Select(r => r.TenantId)
            .Should().BeEquivalentTo([(Guid?)a.Id, b.Id]);
    }

    [Fact]
    public async Task The_sites_member_history_lists_the_access_given_and_stopped_but_not_when_narrowed()
    {
        var (team, manager) = await TeamAsync("Old name");
        var owner = await NewSubjectAsync();
        var site = await NewSiteAsync();
        var end = Now.AddDays(5);
        var (token, _) = await LinkAsync(team, manager);
        (await ApproveAsync(
            token, owner, new TeamAccessGrant(site.Id, [site.Roles[RoleSeeds.Caretaker]], true, end, true))).Refusal.Should().BeNull();
        (await TeamWrites.StopAccessAsync(Contexts(owner), site.Id, team)).Should().BeTrue();
        await Teams(manager).RenameAsync(team, manager, "New name", default);
        var reader = new MemberHistoryReader(Contexts());

        var page = await reader.GetAsync(site.Id, null, 25, 0, Now, default);

        var teamRows = page.Data.Where(e => e.Team is not null).ToList();
        teamRows.Select(e => e.Kind).Should().Equal(MemberHistoryKind.TeamAccessStopped, MemberHistoryKind.TeamAccessGiven);
        teamRows.Should().AllSatisfy(e =>
        {
            e.Team.Should().Be(new MemberHistoryTeam(team, "New name"));
            e.Roles.Should().Equal(RoleSeeds.Caretaker);
            e.HistoryLimit.Should().Be(MemberHistoryLimit.TwentyFourHours);
            e.AccessEndsAt.Should().BeCloseTo(end, TimeSpan.FromMilliseconds(1));
            e.Member.Should().BeNull();
            e.Actor.SubjectId.Should().Be(owner);
        });
        (await reader.GetAsync(site.Id, owner, 25, 0, Now, default)).Data.Should().NotContain(e => e.Team != null);

        (await TeamWrites.DeleteAsync(Contexts(manager), team, manager)).Should().Be(TeamDeletion.Deleted);
        (await reader.GetAsync(site.Id, null, 25, 0, Now, default)).Data.Where(e => e.Team is not null).Last().Team
            .Should().Be(new MemberHistoryTeam(team, "Old name"));
        (await reader.GetAsync(Guid.NewGuid(), null, 25, 0, Now, default)).Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_an_inactive_account_and_a_role_it_holds_at_once_do_not_deadlock()
    {
        var (team, manager) = await TeamAsync();
        var owner = await NewSubjectAsync("owner");
        var site = await NewSiteAsync();
        var (token, _) = await LinkAsync(team, manager, RoleSeeds.Viewer);
        (await ApproveAsync(token, owner, Grant(site, site.Roles[RoleSeeds.Viewer]))).Refusal.Should().BeNull();
        (await Members().AddMemberAsync(new NewTenantMember(site.Id, manager, [site.Roles[RoleSeeds.Caretaker]])
            { How = MembershipJoinHow.Direct, By = null }, default)).Should().BeTrue();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE subjects SET is_active = false WHERE id = @id";
            cmd.Parameters.Add(new NpgsqlParameter("@id", manager));
            await cmd.ExecuteNonQueryAsync();
        }

        var once = new SuppliedContextFactory(() => Context(retry: false));
        await using var holder = Context(retry: false);
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.LockTeamAccessAsync(site.Id, default);

        var roleDeletion = new TenantMembershipRepository(once, AccountLinkRoutes.Off)
            .DeleteRoleAsync(site.Id, site.Roles[RoleSeeds.Caretaker], Now, default);
        await PostgresLockWaits.UntilBlockedAsync(database.AppConnectionString, roleDeletion);
        var accountDeletion = new SubjectRepository(once, AccountLinkRoutes.Off).DeleteAsync(manager, default);
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 2, roleDeletion, accountDeletion);
        await held.RollbackAsync();

        (await roleDeletion.WaitAsync(Eventually.DefaultPatience)).Should().Be(RoleChangeOutcome.Changed);
        (await accountDeletion.WaitAsync(Eventually.DefaultPatience)).Should().BeOfType<SubjectChange.Changed>();
        (await AccessCountAsync(team)).Should().Be(0);
    }
}
