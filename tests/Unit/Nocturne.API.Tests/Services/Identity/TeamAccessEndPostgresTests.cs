using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A site stopping a team's access, the team being deleted, and a staff member being removed or leaving,
/// through <see cref="TeamAccessService"/> on PostgreSQL under the runtime role, for staff who hold a team
/// reason on the access that goes. The teams, their access, their staff and the team reasons that joining
/// and approval give them are real.
/// </summary>
[Trait("Category", "Integration")]
public partial class TeamAccessEndPostgresTests(TeamAccessEndPostgresTests.Database database)
    : IClassFixture<TeamAccessEndPostgresTests.Database>
{
    private static readonly DateTime Now = WholeMicroseconds(DateTime.UtcNow);
    private static readonly CancellationToken Ct = CancellationToken.None;

    public enum Path
    {
        Stop,
        DeleteTeam,
        RemoveStaff,
    }

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("team_access_end");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Site(Guid Tenant, Guid Owner, Guid ViewerRole, Guid CaretakerRole);

    private sealed record Team(Guid Id, string Name, Guid Manager);

    private sealed record Made(Guid Link, Guid Invite, Guid Credential, Guid Channel, Guid ChatLink);

    private sealed record MadeState(
        bool LinkRevoked, bool InviteRevoked, DateTime? CredentialExpires, bool CredentialRevoked, DateTime? ChannelStops,
        bool ChatLinked);

    private sealed record Row(string EventType, string? Details, Guid? Team)
    {
        public string? Read(string key)
        {
            if (Details is null)
                return null;
            using var document = JsonDocument.Parse(Details);
            return document.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
    }

    private static readonly MadeState Untouched = new(false, false, null, false, null, true);

    private static MadeState CappedAt(DateTime end) => new(false, false, end, false, end, true);

    private static readonly MadeState Stopped = new(true, true, Now, false, Now, false);

    private static DateTime WholeMicroseconds(DateTime instant) =>
        new(instant.Ticks - instant.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);

    private NocturneDbContext Context(Guid tenant, Guid? actor)
    {
        var context = TestContexts.Context(database.AppConnectionString)
            .ActingAs(actor is { } subject ? new TestAuditActor { SubjectId = subject } : null);
        context.TenantId = tenant;
        return context;
    }

    private SuppliedContextFactory Contexts(Guid tenant, Guid? actor = null) => new(() => Context(tenant, actor));

    private TeamRepository Teams(Guid? actor = null) => new(Contexts(Guid.Empty, actor));

    private TenantMembershipRepository Members(Guid tenant) => new(Contexts(tenant), AccountLinkRoutes.Off);

    private static GuestSessionCacheService NewSessions() => new(new MemoryCache(new MemoryCacheOptions()));

    private TeamAccessService Service(Guid tenant, Guid actor, GuestSessionCacheService? sessions = null)
    {
        var contexts = Contexts(tenant, actor);
        return new TeamAccessService(
            new TeamRepository(contexts),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            RolesThatMayBeGiven(),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            contexts,
            new GrantRevocationService(sessions ?? NewSessions()),
            Announcer(contexts));
    }

    private readonly RecordingNotices _notices = new();

    private static ITenantRoleService RolesThatMayBeGiven()
    {
        var roles = new Mock<ITenantRoleService>();
        roles.Setup(r => r.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RoleGrantValidation.Valid);
        return roles.Object;
    }

    private TeamStaffAnnouncer Announcer(INocturneDbContextFactory contexts) => new(
        new TeamRepository(contexts),
        new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
        new SiteNotices(new TenantRepository(contexts), _notices.Services()),
        new FakeTimeProvider(new DateTimeOffset(Now)),
        NullLogger<TeamStaffAnnouncer>.Instance);

    private SubjectDeletion Deletion(Guid actor)
    {
        var contexts = Contexts(Guid.Empty, actor);
        return new SubjectDeletion(
            contexts,
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new GrantRevocationService(NewSessions()),
            Mock.Of<IAuthAuditService>(),
            new FakeTimeProvider(new DateTimeOffset(Now)),
            Announcer(contexts),
            InertTeamStaff.Sites(),
            NullLogger<SubjectDeletion>.Instance);
    }

    private Task<Guid> NewSubjectAsync(string name) =>
        new SubjectRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off).CreateAsync(new NewSubject($"{name}-{Guid.NewGuid():N}"), Ct);

    private async Task<Site> SiteAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}-{tenant:N}', 'team-access-end', true, now(), now());
            """);
        var members = Members(tenant);
        await members.SeedRolesAsync(tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var viewer = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Viewer, Ct))!.Value;
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        var owner = await NewSubjectAsync($"{slug}-owner");
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        return new Site(tenant, owner, viewer, caretaker);
    }

    private async Task<Team> TeamAsync(string name)
    {
        var manager = await NewSubjectAsync("manager");
        return new Team((await Teams(manager).CreateAsync(manager, name, Ct)).Id, name, manager);
    }

    /// <summary>Gives the team access to the site, approved by its owner, the team deciding its staff; returns the access.</summary>
    private Task<Guid> FollowAsync(Team team, Site site) =>
        FollowAsync(team, site, new TeamAccessGrant(site.Tenant, [site.ViewerRole], false, null, TeamManagesStaff: true));

    /// <summary>Gives the team the access <paramref name="grant"/> describes, approved by the site's owner; returns the access.</summary>
    private async Task<Guid> FollowAsync(Team team, Site site, TeamAccessGrant grant)
    {
        var approval = await ApproveAsync(site, await AccessLinkAsync(team), grant);
        approval.Refusal.Should().BeNull();
        return approval.AccessId!.Value;
    }

    private async Task<string> AccessLinkAsync(Team team)
    {
        var token = Guid.NewGuid().ToString("N");
        (await Teams(team.Manager).CreateAccessLinkAsync(team.Id, team.Manager, new NewTeamAccessLink(
            token, null, Now.AddDays(1), null, new TeamAccessSuggestion(RoleSeeds.Caretaker, false, null)), Ct)).Should().NotBeNull();
        return token;
    }

    private Task<TeamAccessApproval> ApproveAsync(Site site, string token, TeamAccessGrant grant) =>
        Service(Guid.Empty, site.Owner).ApproveAsync(
            token,
            new TeamAccessApproverCredential(site.Owner, AuthType.SessionCookie, new HashSet<string>(), false),
            grant,
            Ct);

    private async Task<Guid> JoinStaffAsync(Team team, Guid? subject = null)
    {
        var staff = subject ?? await NewSubjectAsync("staff");
        (await JoinAsync(team, staff, new TeamStaffNarrowing(null, false, null))).Refusal.Should().BeNull();
        return staff;
    }

    private async Task<TeamInviteAcceptance> JoinAsync(Team team, Guid staff, TeamStaffNarrowing narrowing)
    {
        var token = await StaffInviteAsync(team, narrowing);
        return await Service(Guid.Empty, staff).JoinStaffAsync(token, staff, Ct);
    }

    private async Task<string> StaffInviteAsync(Team team, TeamStaffNarrowing narrowing)
    {
        var token = Guid.NewGuid().ToString("N");
        (await Teams(team.Manager).CreateStaffInviteAsync(
            team.Id, team.Manager, new NewTeamStaffInvite(token, null, Now.AddDays(1), null, narrowing), Ct)).Should().NotBeNull();
        return token;
    }

    /// <summary>The subject holds the reason <paramref name="access"/> gives, and their row is the sum of their reasons.</summary>
    private async Task HoldsTeamReasonAsync(Site site, Guid subject, Guid access)
    {
        (await KindsAsync(site, subject)).Should().Contain($"{MembershipReasonKind.Team}:{access}");
        await RowIsTheSumAsync(site, subject);
    }

    private async Task DirectMemberAsync(Site site, Guid subject, DateTime? endsAt) =>
        (await Members(site.Tenant).AddMemberAsync(
                new NewTenantMember(site.Tenant, subject, [site.ViewerRole], AccessEndsAt: endsAt) { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();

    /// <summary>A guest link to the owner's data, a member invite, a device credential, an alert channel and a chat link, each made by <paramref name="maker"/> on the site.</summary>
    private async Task<Made> MadeByAsync(Site site, Guid maker)
    {
        var grants = new OAuthGrantRepository(Contexts(site.Tenant));
        var link = await grants.CreateAsync(
            new NewOAuthGrant(
                null, site.Owner, OAuthGrantTypes.Guest, [Scope.GlucoseRead], "link", Guid.NewGuid().ToString("N"), null, null, false,
                CreatedBySubjectId: maker),
            Ct);
        var invite = await new MemberInviteRepository(Contexts(site.Tenant)).CreateAsync(
            new NewMemberInvite(
                site.Tenant, maker, HashUtils.Sha256Hex(Guid.NewGuid().ToString("N")), [site.ViewerRole], null, "Family", false,
                Now.AddDays(7), null, Now, null),
            Ct);
        var device = await new SubjectRepository(Contexts(site.Tenant), AccountLinkRoutes.Off).EnsureDeviceSubjectAsync(Ct);
        var credential = await grants.CreateAsync(
            new NewOAuthGrant(
                null, device, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "uploader", Guid.NewGuid().ToString("N"), null, null, false,
                CreatedBySubjectId: maker),
            Ct);

        var channel = Guid.CreateVersion7();
        var chatLink = Guid.CreateVersion7();
        await using var db = Context(site.Tenant, null);
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = site.Tenant,
            Name = "High",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction":"above","value":180}""",
            IsEnabled = true,
        };
        db.AlertRules.Add(rule);
        db.AlertRuleChannels.Add(new AlertRuleChannelEntity
        {
            Id = channel,
            TenantId = site.Tenant,
            AlertRuleId = rule.Id,
            ChannelType = ChannelType.InApp,
            Destination = Guid.CreateVersion7().ToString(),
            AddedBySubjectId = maker,
        });
        db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
        {
            Id = chatLink,
            Platform = "discord",
            PlatformUserId = Guid.NewGuid().ToString("N"),
            TenantId = site.Tenant,
            NocturneUserId = maker,
            Label = "home",
            DisplayName = "home",
            IsDefault = true,
            IsActive = true,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync(Ct);
        return new Made(link, invite, credential, channel, chatLink);
    }

    private async Task<MadeState> StateAsync(Site site, Made made)
    {
        await using var db = Context(site.Tenant, null);
        var link = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == made.Link, Ct);
        var invite = await db.MemberInvites.AsNoTracking().SingleAsync(i => i.Id == made.Invite, Ct);
        var credential = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == made.Credential, Ct);
        var channel = await db.AlertRuleChannels.AsNoTracking().SingleAsync(c => c.Id == made.Channel, Ct);
        var chatLinked = await db.ChatIdentityDirectory.AsNoTracking().AnyAsync(d => d.Id == made.ChatLink, Ct);
        return new MadeState(
            link.RevokedAt is not null, invite.RevokedAt is not null, credential.ExpiresAt, credential.RevokedAt is not null,
            channel.StopsAt, chatLinked);
    }

    private static GuestSessionCacheService SessionsOf(Site site, params Guid[] grants)
    {
        var sessions = NewSessions();
        foreach (var grant in grants)
            sessions.Set(site.Tenant, grant, null);
        return sessions;
    }

    private async Task<List<string>> KindsAsync(Site site, Guid subject) =>
        await ReadAsync($"""
            SELECT r.kind || ':' || coalesce(r.source_id::text, '') FROM tenant_member_reasons r
              JOIN tenant_members m ON m.id = r.tenant_member_id
             WHERE m.tenant_id = '{site.Tenant}' AND m.subject_id = '{subject}' ORDER BY r.kind
            """);

    private async Task<List<Row>> RowsAsync(Guid tenant, Guid target)
    {
        await using var db = Context(Guid.Empty, null);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.TargetSubjectId == target)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .ToListAsync(Ct))
            .Select(a => new Row(a.EventType, a.DetailsJson, a.TeamId))
            .ToList();
    }

    private async Task<bool> AccessExistsAsync(Guid access) =>
        (await ReadAsync($"SELECT count(*)::text FROM team_tenant_access WHERE id = '{access}'")).Single() == "1";

    private async Task RowIsTheSumAsync(Site site, Guid subject)
    {
        var member = (await Members(site.Tenant).FindMemberAsync(site.Tenant, subject, Ct))!;
        await using var db = Context(site.Tenant, null);
        (await MembershipReasons.MatchesAsync(db, member.Id, Now, Ct)).Should().BeTrue("the row is the sum of its reasons");
    }

    private async Task EndAsync(Path path, Site site, Team team, Guid staff, GuestSessionCacheService? sessions = null)
    {
        switch (path)
        {
            case Path.Stop:
                (await Service(site.Tenant, site.Owner, sessions).StopAsync(site.Tenant, team.Id, Ct)).Should().BeTrue();
                break;
            case Path.DeleteTeam:
                (await Service(Guid.Empty, team.Manager, sessions).DeleteTeamAsync(team.Id, team.Manager, Ct)).Should().Be(TeamDeletion.Deleted);
                break;
            case Path.RemoveStaff:
                (await Service(Guid.Empty, team.Manager, sessions).RemoveStaffAsync(team.Id, team.Manager, staff, Ct)).Should().BeTrue();
                break;
        }
    }

    /// <summary>A site, a team following it, and a subject who has not joined the team yet.</summary>
    private async Task<(Site Site, Team Team, Guid Access, Guid Staff)> SiteAndTeamAsync(string slug)
    {
        var site = await SiteAsync(slug);
        var team = await TeamAsync($"Clinic {slug}");
        var access = await FollowAsync(team, site);
        return (site, team, access, await NewSubjectAsync("staff"));
    }

    /// <summary>A site, a team following it, and a staff member of the team holding its reason there.</summary>
    private async Task<(Site Site, Team Team, Guid Access, Guid Staff)> StaffWithReasonAsync(string slug)
    {
        var (site, team, access, staff) = await SiteAndTeamAsync(slug);
        await JoinStaffAsync(team, staff);
        await HoldsTeamReasonAsync(site, staff, access);
        return (site, team, access, staff);
    }

    [Theory]
    [InlineData(Path.Stop)]
    [InlineData(Path.DeleteTeam)]
    [InlineData(Path.RemoveStaff)]
    public async Task Ending_a_members_only_reason_removes_the_membership_and_stops_what_they_made_at_the_site(Path path)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"only-{path}");
        var made = await MadeByAsync(site, staff);
        var sessions = SessionsOf(site, made.Link, made.Credential);

        await EndAsync(path, site, team, staff, sessions);

        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await KindsAsync(site, staff)).Should().BeEmpty();
        (await StateAsync(site, made)).Should().Be(Stopped);
        sessions.TryGet(site.Tenant, made.Link, out _).Should().BeFalse("the revoked guest link's session is evicted");
        sessions.TryGet(site.Tenant, made.Credential, out _).Should().BeFalse("the capped credential's session is evicted");

        var rows = await RowsAsync(site.Tenant, staff);
        var removed = rows.Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved).Subject;
        removed.Read(MemberAccessAudit.HowKey).Should().Be("team");
        removed.Team.Should().Be(team.Id);
        removed.Read(TeamAudit.NameKey).Should().Be(team.Name);
        var removedAt = rows.IndexOf(removed);
        rows.FindIndex(r => r.EventType == AuthAuditEventType.GuestLinkRevoked).Should().BeLessThan(removedAt);
        rows.FindIndex(r => r.EventType == AuthAuditEventType.MemberInviteRevoked).Should().BeLessThan(removedAt);
        rows.Should().Contain(r => r.EventType == AuthAuditEventType.DeviceCredentialCapped);
        if (path != Path.RemoveStaff)
            (await AccessExistsAsync(access)).Should().BeFalse();
    }

    [Theory]
    [InlineData(Path.Stop)]
    [InlineData(Path.DeleteTeam)]
    [InlineData(Path.RemoveStaff)]
    public async Task A_member_let_in_directly_keeps_the_membership_and_what_they_made_is_capped_at_the_direct_end(Path path)
    {
        var (site, team, access, staff) = await SiteAndTeamAsync($"direct-ends-{path}");
        var directEnds = Now.AddDays(10);
        await DirectMemberAsync(site, staff, directEnds);
        await JoinStaffAsync(team, staff);
        await HoldsTeamReasonAsync(site, staff, access);
        var made = await MadeByAsync(site, staff);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!.AccessEndsAt.Should().BeNull();

        await EndAsync(path, site, team, staff);

        (await KindsAsync(site, staff)).Should().Equal($"{MembershipReasonKind.Direct}:");
        await RowIsTheSumAsync(site, staff);
        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct))!.AccessEndsAt.Should().Be(directEnds);
        (await StateAsync(site, made)).Should().Be(CappedAt(directEnds));
        var removed = (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved).Subject;
        removed.Read(MemberAccessAudit.HowKey).Should().Be("team");
        removed.Team.Should().Be(team.Id);
    }

    [Theory]
    [InlineData(Path.Stop)]
    [InlineData(Path.DeleteTeam)]
    [InlineData(Path.RemoveStaff)]
    public async Task A_member_let_in_directly_with_no_end_keeps_everything_they_made(Path path)
    {
        var (site, team, access, staff) = await SiteAndTeamAsync($"direct-{path}");
        await DirectMemberAsync(site, staff, endsAt: null);
        await JoinStaffAsync(team, staff);
        await HoldsTeamReasonAsync(site, staff, access);
        var made = await MadeByAsync(site, staff);

        await EndAsync(path, site, team, staff);

        (await KindsAsync(site, staff)).Should().Equal($"{MembershipReasonKind.Direct}:");
        await RowIsTheSumAsync(site, staff);
        (await StateAsync(site, made)).Should().Be(Untouched);
    }

    [Theory]
    [InlineData(Path.Stop)]
    [InlineData(Path.DeleteTeam)]
    [InlineData(Path.RemoveStaff)]
    public async Task A_member_on_two_teams_that_follow_the_site_loses_only_the_reason_of_the_team_that_ends(Path path)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"two-teams-{path}");
        var other = await TeamAsync("Ward");
        var otherAccess = await FollowAsync(other, site);
        await JoinStaffAsync(other, staff);
        await HoldsTeamReasonAsync(site, staff, access);
        await HoldsTeamReasonAsync(site, staff, otherAccess);
        var made = await MadeByAsync(site, staff);

        await EndAsync(path, site, team, staff);

        (await KindsAsync(site, staff)).Should().Equal($"{MembershipReasonKind.Team}:{otherAccess}");
        await RowIsTheSumAsync(site, staff);
        (await StateAsync(site, made)).Should().Be(Untouched);
        (await AccessExistsAsync(otherAccess)).Should().BeTrue();
    }

    [Fact]
    public async Task A_staff_member_who_leaves_is_recorded_at_the_site_as_ended_by_the_team_not_as_leaving()
    {
        var (site, team, access, staff) = await SiteAndTeamAsync("leave");
        await DirectMemberAsync(site, staff, endsAt: null);
        await JoinStaffAsync(team, staff);
        await HoldsTeamReasonAsync(site, staff, access);

        (await Service(Guid.Empty, staff).RemoveStaffAsync(team.Id, staff, staff, Ct)).Should().BeTrue();

        var removed = (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved).Subject;
        removed.Read(MemberAccessAudit.HowKey).Should().Be("team");
        (await KindsAsync(site, staff)).Should().Equal($"{MembershipReasonKind.Direct}:");
    }

    [Fact]
    public async Task Deleting_a_team_on_two_sites_ends_each_membership_and_stops_what_the_member_made_on_each_site()
    {
        var first = await SiteAsync("delete-two-a");
        var second = await SiteAsync("delete-two-b");
        var team = await TeamAsync("Clinic");
        var firstAccess = await FollowAsync(team, first);
        var secondAccess = await FollowAsync(team, second);
        var staff = await JoinStaffAsync(team);
        await HoldsTeamReasonAsync(first, staff, firstAccess);
        await HoldsTeamReasonAsync(second, staff, secondAccess);
        var madeFirst = await MadeByAsync(first, staff);
        var madeSecond = await MadeByAsync(second, staff);

        (await Service(Guid.Empty, team.Manager).DeleteTeamAsync(team.Id, team.Manager, Ct)).Should().Be(TeamDeletion.Deleted);

        foreach (var (site, made) in new[] { (first, madeFirst), (second, madeSecond) })
        {
            (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
            (await StateAsync(site, made)).Should().Be(Stopped, "the stop read the site's rows under its own tenant");
            var rows = await RowsAsync(site.Tenant, staff);
            rows.Should().ContainSingle(r => r.EventType == AuthAuditEventType.GuestLinkRevoked);
            rows.Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberInviteRevoked);
            rows.Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved);
        }
    }

    [Fact]
    public async Task A_stop_and_a_staff_removal_on_the_same_site_at_once_both_finish_and_end_the_membership_once()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("race-stop-remove");
        var made = await MadeByAsync(site, staff);

        await RaceAsync(site, staff,
            () => Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct),
            () => Service(Guid.Empty, team.Manager).RemoveStaffAsync(team.Id, team.Manager, staff, Ct));

        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await AccessExistsAsync(access)).Should().BeFalse();
        (await Teams().GetTeamsOfAsync(staff, Ct)).Should().BeEmpty();
        (await StateAsync(site, made)).Should().Be(Stopped);
        (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved);
    }

    [Fact]
    public async Task A_team_deletion_and_a_staff_removal_at_once_both_finish_and_end_the_membership_once()
    {
        var (site, team, access, staff) = await StaffWithReasonAsync("race-delete-remove");

        await RaceAsync(site, staff,
            () => Service(Guid.Empty, team.Manager).DeleteTeamAsync(team.Id, team.Manager, Ct),
            () => Service(Guid.Empty, team.Manager).RemoveStaffAsync(team.Id, team.Manager, staff, Ct));

        (await Members(site.Tenant).FindMemberAsync(site.Tenant, staff, Ct)).Should().BeNull();
        (await AccessExistsAsync(access)).Should().BeFalse();
        (await RowsAsync(site.Tenant, staff)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stop_and_the_staff_members_account_deletion_at_once_both_finish(bool evenLastOwner)
    {
        var (site, team, access, staff) = await StaffWithReasonAsync($"race-stop-delete-{evenLastOwner}");

        await RaceAsync(site, staff,
            () => Service(site.Tenant, site.Owner).StopAsync(site.Tenant, team.Id, Ct),
            () => Deletion(staff).DeleteAsync(staff, evenLastOwner, Ct));

        (await new SubjectRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off).GetAsync(staff, Ct)).Should().BeNull();
        (await AccessExistsAsync(access)).Should().BeFalse();
        (await ReadAsync($"SELECT count(*)::text FROM tenant_members WHERE subject_id = '{staff}'")).Should().Equal("0");
    }

    /// <summary>
    /// <see cref="RaceAsync(Site, Func{NocturneDbContext, Task}, Func{Task}, Func{Task})"/> holding the staff
    /// member's membership lock on the site, so the first is ahead of the second at the membership.
    /// </summary>
    private Task RaceAsync(Site site, Guid staff, Func<Task> first, Func<Task> second) =>
        RaceAsync(site, holder => holder.LockMembershipAsync(site.Tenant, staff, Ct), first, second);

    /// <summary>
    /// Holds what <paramref name="hold"/> locks, starts <paramref name="first"/> and waits for it to queue,
    /// starts <paramref name="second"/> and waits for it to queue, then lets both run. Each must finish
    /// without an exception.
    /// </summary>
    private async Task RaceAsync(Site site, Func<NocturneDbContext, Task> hold, Func<Task> first, Func<Task> second)
    {
        await using var holder = Context(site.Tenant, null);
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        await hold(holder);

        var ahead = first();
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 1, ahead);
        var behind = second();
        await PostgresLockWaits.UntilWaitingAsync(database.AppConnectionString, 2, ahead, behind);
        await held.RollbackAsync(Ct);

        await ahead.WaitAsync(Eventually.DefaultPatience);
        await behind.WaitAsync(Eventually.DefaultPatience);
    }

    private async Task<List<string>> ReadAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
