using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
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
/// The paths that let a person in directly, remove them and list them, on PostgreSQL under the runtime
/// role, for a member who also holds, or only holds, a team's reason. The team reasons are written
/// straight to the tables, with the staff rows they stand on, and projected by the startup check.
/// </summary>
[Trait("Category", "Integration")]
public class DirectReasonBesideTeamPostgresTests(DirectReasonBesideTeamPostgresTests.Database database)
    : IClassFixture<DirectReasonBesideTeamPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("direct_beside_team");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Row(string EventType, string? Details)
    {
        public string? How
        {
            get
            {
                if (Details is null)
                    return null;
                using var document = JsonDocument.Parse(Details);
                return document.RootElement.TryGetProperty("how", out var value) ? value.GetString() : null;
            }
        }
    }

    private sealed record Seeded(Guid Tenant, Guid Manager, Guid Person, Guid ViewerRole, Guid CaretakerRole, Guid AdminRole);

    private NocturneDbContext Context(Guid tenant, Guid? actor)
    {
        var context = TestContexts.Context(database.AppConnectionString)
            .ActingAs(actor is { } subject ? new TestAuditActor { SubjectId = subject } : null);
        context.TenantId = tenant;
        return context;
    }

    private SuppliedContextFactory Contexts(Guid tenant, Guid? actor = null) => new(() => Context(tenant, actor));

    private async Task<Seeded> SeedAsync(string slug, bool personPending = false)
    {
        var tenant = Guid.NewGuid();
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
            VALUES ('{tenant}', '{slug}-{tenant:N}', 'direct-beside-team', true, now(), now());
            """);

        var contexts = Contexts(tenant);
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, Ct);
        var viewer = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Viewer, Ct))!.Value;
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        var owner = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var admin = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Admin, Ct))!.Value;
        var subjects = new SubjectRepository(contexts, AccountLinkRoutes.Off);
        var manager = await subjects.CreateAsync(new NewSubject($"{slug}-manager"), Ct);
        var person = await subjects.CreateAsync(
            personPending
                ? new NewSubject($"{slug}-person", IsActive: false, ApprovalStatus: "Pending")
                : new NewSubject($"{slug}-person"),
            Ct);
        (await members.AddMemberAsync(new NewTenantMember(tenant, manager, [owner]) { How = MembershipJoinHow.Direct, By = null }, Ct)).Should().BeTrue();
        return new Seeded(tenant, manager, person, viewer, caretaker, admin);
    }

    /// <summary>
    /// Gives <paramref name="subject"/> the reason of a new team's access to <paramref name="tenant"/>
    /// giving <paramref name="role"/> until <paramref name="endsAt"/>, with the staff rows it stands on,
    /// adding the row when they hold none, and projects it as the startup check does. A reason of a team
    /// with access to another site (<paramref name="accessTenant"/>) stands on nothing at this one, so it
    /// is written alone and the check, which would remove it, does not run.
    /// </summary>
    private async Task<Guid> TeamReasonAsync(
        Guid tenant, Guid subject, Guid role, string teamName, bool limitTo24Hours = false, Guid? accessTenant = null,
        DateTime? endsAt = null)
    {
        var members = new TenantMembershipRepository(Contexts(tenant), AccountLinkRoutes.Off);
        var existing = (await members.FindMemberAsync(tenant, subject, Ct))?.Id;
        var member = existing ?? Guid.CreateVersion7();
        var team = Guid.CreateVersion7();
        var access = Guid.CreateVersion7();
        var reason = Guid.CreateVersion7();
        var limit = limitTo24Hours ? "true" : "false";
        var ends = endsAt is { } end ? $"'{end:O}'" : "NULL";
        var at = accessTenant ?? tenant;
        var teamMember = Guid.CreateVersion7();
        var staff = accessTenant is null
            ? $"""
                INSERT INTO team_tenant_access_roles (id, team_tenant_access_id, tenant_role_id)
                VALUES ('{Guid.CreateVersion7()}', '{access}', '{role}');
                INSERT INTO team_members (id, team_id, subject_id, kind, limit_to_24_hours, ends_at)
                VALUES ('{teamMember}', '{team}', '{subject}', 'staff', false, {ends});
                INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
                VALUES ('{Guid.CreateVersion7()}', '{access}', '{teamMember}', 'approved');
                """
            : string.Empty;
        await NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
            SELECT set_config('app.current_tenant_id', '{at}', false);
            INSERT INTO teams (id, name) VALUES ('{team}', '{teamName}');
            INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
            VALUES ('{access}', '{team}', '{at}', {limit});
            {staff}
            SELECT set_config('app.current_tenant_id', '{tenant}', false);
            {(existing is null ? $"""
                INSERT INTO tenant_members (id, tenant_id, subject_id, limit_to_24_hours, sys_created_at, sys_updated_at)
                VALUES ('{member}', '{tenant}', '{subject}', {limit}, now(), now());
                """ : "")}
            INSERT INTO tenant_member_reasons (id, tenant_member_id, kind, source_id, limit_to_24_hours, access_ends_at)
            VALUES ('{reason}', '{member}', 'team', '{access}', {limit}, {ends});
            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id)
            VALUES ('{Guid.CreateVersion7()}', '{reason}', '{role}');
            """);
        if (accessTenant is null)
            await ReconcileAsync();
        return team;
    }

    private Task ReconcileAsync() =>
        DatabaseInitializationExtensions.ReconcileMembershipReasonsAsync(
            database.MigratorConnectionString, Now.UtcDateTime,
            (unit, changes, ct) => TeamReasonWrites.ApplyAsync(unit, changes, Revocation(), Now.UtcDateTime, ct),
            NullLogger.Instance, Ct);

    private async Task<List<string>> KindsAsync(Guid tenant, Guid subject) =>
        await ReadAsync($"""
            SELECT r.kind FROM tenant_member_reasons r JOIN tenant_members m ON m.id = r.tenant_member_id
             WHERE m.tenant_id = '{tenant}' AND m.subject_id = '{subject}' ORDER BY r.kind
            """);

    private async Task<List<Guid>> RoleIdsAsync(Guid tenant, Guid subject) =>
        [.. (await new TenantMembershipRepository(Contexts(tenant), AccountLinkRoutes.Off).FindMemberAsync(tenant, subject, Ct))!
            .Roles.Select(r => r.RoleId)];

    private async Task<List<Row>> RowsAsync(Guid tenant, Guid target)
    {
        await using var db = Context(Guid.Empty, null);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.TargetSubjectId == target)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .ToListAsync())
            .Select(a => new Row(a.EventType, a.DetailsJson))
            .ToList();
    }

    private static GrantRevocationService Revocation() =>
        new(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())));

    private static MemberInviteService InviteService(SuppliedContextFactory contexts) => new(
        new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
        new MemberInviteRepository(contexts),
        new SubjectRepository(contexts, AccountLinkRoutes.Off),
        contexts,
        Mock.Of<IJwtService>(),
        Mock.Of<ITenantRoleService>(),
        Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
        new FakeTimeProvider(Now),
        NullLogger<MemberInviteService>.Instance,
        AccountLinkRoutes.Off,
        Revocation(), InertTeamStaff.Announcer());

    private static ITenantRoleService GrantsEverything() =>
        Mock.Of<ITenantRoleService>(r => r.ValidateRoleGrantAsync(
                It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())
            == Task.FromResult(RoleGrantValidation.Valid));

    private static MembershipRequestService RequestService(SuppliedContextFactory contexts) => new(
        new TenantRepository(contexts),
        new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
        new SubjectRepository(contexts, AccountLinkRoutes.Off),
        contexts,
        GrantsEverything(),
        Mock.Of<IInAppNotificationService>(),
        Mock.Of<IAuthAuditService>(),
        new FakeTimeProvider(Now),
        NullLogger<MembershipRequestService>.Instance,
        AccountLinkRoutes.Off,
        Revocation(), InertTeamStaff.Announcer());

    private static TenantService TenantService(SuppliedContextFactory contexts, GuestSessionCacheService? sessions = null) => new(
        contexts,
        new TenantRepository(contexts),
        new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
        new SubjectRepository(contexts, AccountLinkRoutes.Off),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        sessions is null ? Revocation() : new GrantRevocationService(sessions),
        new FakeTimeProvider(Now),
        Mock.Of<ILogger<TenantService>>(),
        AccountLinkRoutes.Off, InertTeamStaff.Announcer());

    private Task<string> InviteAsync(Seeded s) => InviteByAsync(s, s.Manager);

    private async Task<string> InviteByAsync(Seeded s, Guid maker)
    {
        var token = $"token-{Guid.NewGuid():N}";
        await new MemberInviteRepository(Contexts(s.Tenant)).CreateAsync(
            new NewMemberInvite(
                s.Tenant, maker, HashUtils.Sha256Hex(token), [s.ViewerRole], null, "Family", false,
                Now.UtcDateTime.AddDays(7), null, Now.UtcDateTime, null),
            Ct);
        return token;
    }

    private async Task RowIsTheSumAsync(Seeded s, Guid subject)
    {
        var member = (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, subject, Ct))!;
        await using var db = Context(s.Tenant, null);
        (await MembershipReasons.MatchesAsync(db, member.Id, Now.UtcDateTime, Ct)).Should().BeTrue("the row is the sum of its reasons");
    }

    /// <summary>A device credential and an alert channel <see cref="Seeded.Person"/> made, neither with an end.</summary>
    private async Task<(Guid Credential, Guid Channel)> MadeByPersonAsync(Seeded s)
    {
        var device = await new SubjectRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).EnsureDeviceSubjectAsync(Ct);
        var credential = await new OAuthGrantRepository(Contexts(s.Tenant)).CreateAsync(
            new NewOAuthGrant(
                null, device, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "uploader", Guid.NewGuid().ToString("N"), null, null, false,
                CreatedBySubjectId: s.Person),
            Ct);
        var channel = Guid.CreateVersion7();
        await using var db = Context(s.Tenant, null);
        var rule = new AlertRuleEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = s.Tenant,
            Name = "High",
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = """{"direction":"above","value":180}""",
            IsEnabled = true,
        };
        db.AlertRules.Add(rule);
        db.AlertRuleChannels.Add(new AlertRuleChannelEntity
        {
            Id = channel,
            TenantId = s.Tenant,
            AlertRuleId = rule.Id,
            ChannelType = ChannelType.InApp,
            Destination = Guid.CreateVersion7().ToString(),
            AddedBySubjectId = s.Person,
        });
        await db.SaveChangesAsync(Ct);
        return (credential, channel);
    }

    /// <summary>The credential's expiry and whether it is revoked, and when the channel stops.</summary>
    private async Task<(DateTime? Expires, bool Revoked, DateTime? Stops)> MadeStateAsync(Seeded s, (Guid Credential, Guid Channel) made)
    {
        await using var db = Context(s.Tenant, null);
        var grant = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == made.Credential, Ct);
        var channel = await db.AlertRuleChannels.AsNoTracking().SingleAsync(c => c.Id == made.Channel, Ct);
        return (grant.ExpiresAt, grant.RevokedAt is not null, channel.StopsAt);
    }

    /// <summary>A guest link to <see cref="Seeded.Manager"/>'s data that <see cref="Seeded.Person"/> made, with no end.</summary>
    private async Task<Guid> GuestLinkByPersonAsync(Seeded s) =>
        await new OAuthGrantRepository(Contexts(s.Tenant)).CreateAsync(
            new NewOAuthGrant(
                null, s.Manager, OAuthGrantTypes.Guest, [Scope.GlucoseRead], "link", Guid.NewGuid().ToString("N"), null, null, false,
                CreatedBySubjectId: s.Person),
            Ct);

    private async Task<(DateTime? Expires, bool Revoked)> LinkStateAsync(Seeded s, Guid link)
    {
        await using var db = Context(s.Tenant, null);
        var grant = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == link, Ct);
        return (grant.ExpiresAt, grant.RevokedAt is not null);
    }

    private async Task<DateTime?> AccessEndAsync(Seeded s) =>
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, s.Person, Ct))!.AccessEndsAt;

    /// <summary>Two sites, in the order the startup check reaches them, which is by tenant id.</summary>
    private async Task<(Seeded First, Seeded Second)> TwoSitesAsync(string slug)
    {
        var a = await SeedAsync($"{slug}-a");
        var b = await SeedAsync($"{slug}-b");
        return string.CompareOrdinal(a.Tenant.ToString(), b.Tenant.ToString()) < 0 ? (a, b) : (b, a);
    }

    /// <summary>
    /// Makes <see cref="Seeded.Person"/> a member only through a team's reason ending at
    /// <paramref name="teamEnds"/>, then has them make a guest link, a device credential and an alert channel.
    /// </summary>
    private async Task<(Guid Link, (Guid Credential, Guid Channel) Made)> TeamOnlyWithMadeAsync(Seeded s, DateTime teamEnds)
    {
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        return (await GuestLinkByPersonAsync(s), await MadeByPersonAsync(s));
    }

    private Task SetChannelTypeAsync(Seeded s, Guid channel, string channelType) => NpgsqlTestCommands.ExecuteAsync(
        database.MigratorConnectionString, $"""
        SELECT set_config('app.current_tenant_id', '{s.Tenant}', false);
        UPDATE alert_rule_channels SET channel_type = '{channelType}' WHERE id = '{channel}';
        """);

    /// <summary>Clears the end of <see cref="Seeded.Person"/>'s row, as a build that knows nothing of reasons would.</summary>
    private Task ClearAccessEndAsync(Seeded s) => NpgsqlTestCommands.ExecuteAsync(database.MigratorConnectionString, $"""
        SELECT set_config('app.current_tenant_id', '{s.Tenant}', false);
        UPDATE tenant_members SET access_ends_at = NULL WHERE tenant_id = '{s.Tenant}' AND subject_id = '{s.Person}';
        """);

    private async Task<Guid> AccessOfTeamAsync(Seeded s, Guid team) =>
        Guid.Parse((await ReadInTenantAsync(s.Tenant, $"SELECT id::text FROM team_tenant_access WHERE team_id = '{team}'")).Single());

    private static GuestSessionCacheService SessionsOf(Seeded s, params Guid[] grants)
    {
        var sessions = new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()));
        foreach (var grant in grants)
            sessions.Set(s.Tenant, grant, null);
        return sessions;
    }

    private static bool IsCached(GuestSessionCacheService sessions, Seeded s, Guid grant) => sessions.TryGet(s.Tenant, grant, out _);

    [Fact]
    public async Task A_staff_member_accepts_an_invite_and_holds_the_sum_of_both_reasons()
    {
        var s = await SeedAsync("invite-staff");
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(await InviteAsync(s), s.Person, s.Tenant)).Success.Should().BeTrue();

        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
        (await RoleIdsAsync(s.Tenant, s.Person)).Should().BeEquivalentTo([s.ViewerRole, s.CaretakerRole]);
        (await RowsAsync(s.Tenant, s.Person)).Select(r => r.EventType).Should().Equal(
            AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned);
    }

    [Fact]
    public async Task An_invite_is_still_refused_to_a_member_let_in_directly_who_is_also_staff()
    {
        var s = await SeedAsync("invite-mixed");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");

        var result = await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(await InviteAsync(s), s.Person, s.Tenant);

        result.ErrorCode.Should().Be("already_member");
        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task An_invite_replaces_an_ended_direct_reason_and_keeps_the_teams()
    {
        var s = await SeedAsync("invite-rejoin");
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                new NewTenantMember(s.Tenant, s.Person, [s.ViewerRole], AccessEndsAt: Now.UtcDateTime.AddDays(-1))
                    { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var before = (await RowsAsync(s.Tenant, s.Person)).Count;

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(await InviteAsync(s), s.Person, s.Tenant)).Success.Should().BeTrue();

        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        var member = (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, s.Person, Ct))!;
        member.DirectReason!.AccessEndsAt.Should().BeNull("the invite's direct reason replaced the ended one");
        await RowIsTheSumAsync(s, s.Person);
        var rows = (await RowsAsync(s.Tenant, s.Person)).Skip(before).ToList();
        rows.Where(r => r.EventType is AuthAuditEventType.MemberRemoved or AuthAuditEventType.MemberJoined).Select(r => r.How)
            .Should().Equal("replaced", "invite");
    }

    [Fact]
    public async Task Approving_a_membership_request_from_a_staff_member_adds_a_direct_reason()
    {
        var s = await SeedAsync("request-staff");
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var request = await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off)
            .CreateRequestAsync(new NewMembershipRequest(s.Tenant, s.Person, null, Now.UtcDateTime), Ct);

        (await RequestService(Contexts(s.Tenant, s.Manager))
                .ApproveRequestAsync(request, s.Tenant, [s.ViewerRole], s.Manager, [Scope.FullAccess], accessEndsAt: null))
            .Error.Should().BeNull();

        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
        (await RoleIdsAsync(s.Tenant, s.Person)).Should().BeEquivalentTo([s.ViewerRole, s.CaretakerRole]);
    }

    [Fact]
    public async Task Approving_a_membership_request_is_still_refused_for_a_member_let_in_directly()
    {
        var s = await SeedAsync("request-mixed");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var request = await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off)
            .CreateRequestAsync(new NewMembershipRequest(s.Tenant, s.Person, null, Now.UtcDateTime), Ct);

        (await RequestService(Contexts(s.Tenant, s.Manager))
                .ApproveRequestAsync(request, s.Tenant, [s.CaretakerRole], s.Manager, [Scope.FullAccess], accessEndsAt: null))
            .Success.Should().BeFalse();
    }

    [Fact]
    public async Task Approving_an_access_request_from_a_staff_member_adds_a_direct_reason()
    {
        var s = await SeedAsync("access-staff", personPending: true);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var service = new AccessRequestService(
            new SubjectRepository(Contexts(s.Tenant, s.Manager), AccountLinkRoutes.Off),
            new TenantMembershipRepository(Contexts(s.Tenant, s.Manager), AccountLinkRoutes.Off),
            Contexts(s.Tenant, s.Manager),
            GrantsEverything(),
            Mock.Of<IInAppNotificationService>(),
            new FakeTimeProvider(Now),
            NullLogger<AccessRequestService>.Instance,
            AccountLinkRoutes.Off,
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))), InertTeamStaff.Announcer());

        (await service.ApproveAsync(s.Person, s.Tenant, [s.ViewerRole], null, false, [Scope.FullAccess], s.Manager))
            .Outcome.Should().Be(ApproveAccessRequestOutcome.Approved);

        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task A_manager_adds_a_staff_member_directly_and_cannot_add_a_member_let_in_directly_again()
    {
        var s = await SeedAsync("direct-staff");
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var service = TenantService(Contexts(s.Tenant, s.Manager));

        (await service.AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole])).Should().BeTrue();
        (await service.AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole])).Should().BeFalse();

        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task Removing_a_mixed_member_removes_the_direct_reason_and_what_was_made_under_it_and_keeps_the_teams_access()
    {
        var s = await SeedAsync("remove-mixed");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var grants = new OAuthGrantRepository(Contexts(s.Tenant));
        await grants.CreateAsync(
            new NewOAuthGrant(
                null, s.Manager, OAuthGrantTypes.Guest, [Scope.GlucoseRead], "link", Guid.NewGuid().ToString("N"), null,
                Now.UtcDateTime.AddDays(2), false, CreatedBySubjectId: s.Person),
            Ct);
        await grants.CreateAsync(
            new NewOAuthGrant(null, s.Person, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "token", Guid.NewGuid().ToString("N"), null, null, false),
            Ct);
        await using (var db = Context(s.Tenant, null))
        {
            db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
            {
                Id = Guid.CreateVersion7(), Platform = "discord", PlatformUserId = $"u-{Guid.NewGuid():N}", TenantId = s.Tenant,
                NocturneUserId = s.Person, Label = "Home", DisplayName = "Home",
            });
            await db.SaveChangesAsync(Ct);
        }

        (await TenantService(Contexts(s.Tenant, s.Manager)).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
        (await RoleIdsAsync(s.Tenant, s.Person)).Should().Equal(s.CaretakerRole);
        (await ReadInTenantAsync(s.Tenant, $"SELECT count(*)::text FROM oauth_grants WHERE (subject_id = '{s.Person}' OR created_by_subject_id = '{s.Person}') AND revoked_at IS NULL"))
            .Should().Equal("0");
        (await ReadInTenantAsync(s.Tenant, $"SELECT count(*)::text FROM chat_identity_directory WHERE nocturne_user_id = '{s.Person}'")).Should().Equal("0");
        (await RowsAsync(s.Tenant, s.Person))[^1].Should().Match<Row>(r => r.EventType == AuthAuditEventType.MemberRemoved && r.How == "removed");
    }

    [Fact]
    public async Task Removing_a_team_only_member_is_refused_as_access_from_a_team_and_writes_nothing()
    {
        var s = await SeedAsync("remove-staff");
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        await new OAuthGrantRepository(Contexts(s.Tenant)).CreateAsync(
            new NewOAuthGrant(
                null, s.Manager, OAuthGrantTypes.Guest, [Scope.GlucoseRead], "link", Guid.NewGuid().ToString("N"), null,
                Now.UtcDateTime.AddDays(2), false, CreatedBySubjectId: s.Person),
            Ct);
        var before = await RowsAsync(s.Tenant, s.Person);

        var result = await TenantService(Contexts(s.Tenant, s.Manager)).RemoveMemberAsync(s.Tenant, s.Person);

        result.Should().Match<MemberRemovalResult>(r => !r.Ok && r.AccessFromTeam);
        (await RowsAsync(s.Tenant, s.Person)).Should().HaveCount(before.Count);
        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
        (await ReadInTenantAsync(s.Tenant, $"SELECT count(*)::text FROM oauth_grants WHERE created_by_subject_id = '{s.Person}' AND revoked_at IS NULL"))
            .Should().Equal("1");
    }

    [Fact]
    public async Task The_member_list_says_what_each_member_holds_and_why()
    {
        var s = await SeedAsync("list");
        var service = TenantService(Contexts(s.Tenant, s.Manager));
        var subjects = new SubjectRepository(Contexts(s.Tenant), AccountLinkRoutes.Off);
        var staff = await subjects.CreateAsync(new NewSubject("list-staff"), Ct);
        var foreign = await subjects.CreateAsync(new NewSubject("list-foreign"), Ct);
        var otherSite = (await SeedAsync("list-other")).Tenant;
        (await service.AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole])).Should().BeTrue();
        var mixedTeam = await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", limitTo24Hours: true);
        await TeamReasonAsync(s.Tenant, staff, s.CaretakerRole, "Camp");
        (await service.AddMemberAsync(s.Tenant, foreign, [s.ViewerRole])).Should().BeTrue();
        await TeamReasonAsync(s.Tenant, foreign, s.CaretakerRole, "Elsewhere", accessTenant: otherSite);

        var members = (await service.GetByIdAsync(s.Tenant))!.Members;

        var manager = members.Single(m => m.SubjectId == s.Manager);
        manager.HasDirect.Should().BeTrue();
        manager.Reasons.Should().ContainSingle().Which.Kind.Should().Be(MemberReasonKind.Direct);
        manager.DirectLiftsTeamLimit.Should().BeFalse();

        var mixed = members.Single(m => m.SubjectId == s.Person);
        mixed.HasDirect.Should().BeTrue();
        mixed.Reasons.Select(r => r.Kind).Should().Equal(MemberReasonKind.Direct, MemberReasonKind.Team);
        mixed.Reasons[1].Should().Match<TenantMemberReasonDto>(r =>
            r.TeamId == mixedTeam && r.TeamName == "Clinic" && r.LimitTo24Hours && !r.HasEnded
            && r.Roles.Single().RoleId == s.CaretakerRole);
        mixed.Reasons[0].Roles.Select(r => r.RoleId).Should().Equal(s.ViewerRole);
        mixed.Roles.Select(r => r.RoleId).Should().BeEquivalentTo([s.ViewerRole, s.CaretakerRole]);
        mixed.LimitTo24Hours.Should().BeFalse();
        mixed.DirectLiftsTeamLimit.Should().BeTrue();

        var teamOnly = members.Single(m => m.SubjectId == staff);
        teamOnly.HasDirect.Should().BeFalse();
        teamOnly.Reasons.Should().ContainSingle().Which.Should().Match<TenantMemberReasonDto>(r =>
            r.Kind == MemberReasonKind.Team && r.TeamName == "Camp");

        var foreignMember = members.Single(m => m.SubjectId == foreign);
        foreignMember.Reasons.Should().ContainSingle().Which.Kind.Should().Be(MemberReasonKind.Direct);
        foreignMember.Reasons.Should().NotContain(r => r.TeamName == "Elsewhere");
    }

    [Fact]
    public async Task A_staff_member_whose_team_gives_members_invite_cannot_accept_an_invite_they_made()
    {
        var s = await SeedAsync("own-invite-staff");
        await TeamReasonAsync(s.Tenant, s.Person, s.AdminRole, "Clinic");
        var token = await InviteByAsync(s, s.Person);
        var before = (await RowsAsync(s.Tenant, s.Person)).Count;

        var result = await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Person, s.Tenant);

        result.Should().Match<AcceptMemberInviteResult>(r => !r.Success && r.ErrorCode == SelfAdmission.OwnInviteCode);
        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Team);
        (await RowsAsync(s.Tenant, s.Person)).Should().HaveCount(before);
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task A_member_let_in_directly_who_accepts_an_invite_they_made_is_refused_as_already_a_member()
    {
        var s = await SeedAsync("own-invite-direct");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole]);

        var result = await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(await InviteByAsync(s, s.Person), s.Person, s.Tenant);

        result.ErrorCode.Should().Be("already_member");
    }

    [Fact]
    public async Task A_staff_member_whose_team_gives_members_manage_cannot_approve_their_own_request()
    {
        var s = await SeedAsync("own-request-staff");
        await TeamReasonAsync(s.Tenant, s.Person, s.AdminRole, "Clinic");
        var members = new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off);
        var request = await members.CreateRequestAsync(new NewMembershipRequest(s.Tenant, s.Person, null, Now.UtcDateTime), Ct);

        var result = await RequestService(Contexts(s.Tenant, s.Person))
            .ApproveRequestAsync(request, s.Tenant, [s.ViewerRole], s.Person, RoleSeeds.Permissions[RoleSeeds.Admin], accessEndsAt: null);

        result.Should().Match<DecideMembershipRequestResult>(r => !r.Success && r.Code == SelfAdmission.OwnRequestCode);
        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Team);
        (await members.GetRequestAsync(s.Tenant, request, Ct))!.Status.Should().Be(MembershipRequestRecord.Pending);
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task A_member_let_in_directly_who_approves_their_own_request_is_refused_as_already_a_member()
    {
        var s = await SeedAsync("own-request-direct");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.AdminRole]);
        var request = await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off)
            .CreateRequestAsync(new NewMembershipRequest(s.Tenant, s.Person, null, Now.UtcDateTime), Ct);

        var result = await RequestService(Contexts(s.Tenant, s.Person))
            .ApproveRequestAsync(request, s.Tenant, [s.ViewerRole], s.Person, RoleSeeds.Permissions[RoleSeeds.Admin], accessEndsAt: null);

        result.Should().Match<DecideMembershipRequestResult>(r => !r.Success && r.Code == null && r.Error == "The subject is already a member.");
    }

    [Fact]
    public async Task Replacing_an_ended_direct_reason_beside_a_teams_in_force_leaves_what_the_person_made()
    {
        var s = await SeedAsync("rejoin-keeps");
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                new NewTenantMember(s.Tenant, s.Person, [s.ViewerRole], AccessEndsAt: Now.UtcDateTime.AddDays(-1))
                    { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var made = await MadeByPersonAsync(s);

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(await InviteAsync(s), s.Person, s.Tenant)).Success.Should().BeTrue();

        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)null, false, (DateTime?)null));
        (await KindsAsync(s.Tenant, s.Person)).Should().Equal(MembershipReasonKind.Direct, MembershipReasonKind.Team);
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task An_end_on_the_direct_reason_beside_an_unending_teams_caps_nothing_the_person_made()
    {
        var s = await SeedAsync("end-unending-team");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var made = await MadeByPersonAsync(s);
        var member = (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, s.Person, Ct))!;

        (await TenantService(Contexts(s.Tenant, s.Manager)).SetMemberAccessEndAsync(s.Tenant, member.Id, Now.UtcDateTime.AddDays(10), Now.UtcDateTime))
            .Should().Be(OwnerGuardedChange.Changed);

        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)null, false, (DateTime?)null));
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task An_end_on_the_direct_reason_caps_what_the_person_made_at_the_teams_end_when_that_is_later()
    {
        var s = await SeedAsync("end-later-team");
        var teamEnds = Now.UtcDateTime.AddDays(20);
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        var made = await MadeByPersonAsync(s);
        var member = (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, s.Person, Ct))!;

        (await TenantService(Contexts(s.Tenant, s.Manager)).SetMemberAccessEndAsync(s.Tenant, member.Id, Now.UtcDateTime.AddDays(10), Now.UtcDateTime))
            .Should().Be(OwnerGuardedChange.Changed);

        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task An_end_on_a_direct_only_member_still_caps_what_they_made_at_that_end()
    {
        var s = await SeedAsync("end-direct-only");
        var ends = Now.UtcDateTime.AddDays(10);
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        var made = await MadeByPersonAsync(s);
        var member = (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, s.Person, Ct))!;

        (await TenantService(Contexts(s.Tenant, s.Manager)).SetMemberAccessEndAsync(s.Tenant, member.Id, ends, Now.UtcDateTime))
            .Should().Be(OwnerGuardedChange.Changed);

        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)ends, false, (DateTime?)ends));
    }

    [Fact]
    public async Task Removing_a_direct_reason_with_no_end_caps_what_the_person_made_at_the_teams_end_and_evicts_it_after_the_commit()
    {
        var s = await SeedAsync("remove-ending-team");
        var teamEnds = Now.UtcDateTime.AddDays(20);
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);
        (await AccessEndAsync(s)).Should().BeNull();
        var sessions = SessionsOf(s, link, made.Credential);

        (await TenantService(Contexts(s.Tenant, s.Manager), sessions).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await AccessEndAsync(s)).Should().Be(teamEnds);
        (await LinkStateAsync(s, link)).Revoked.Should().BeTrue("removal revokes every guest link the person made");
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        IsCached(sessions, s, link).Should().BeFalse();
        IsCached(sessions, s, made.Credential).Should().BeFalse("the capped credential is evicted with the revoked link");
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task Removing_a_direct_reason_with_an_end_beside_a_later_teams_lets_what_the_person_made_last_to_the_teams_end()
    {
        var s = await SeedAsync("remove-ended-direct-kept");
        var directEnds = Now.UtcDateTime.AddDays(10);
        var teamEnds = Now.UtcDateTime.AddDays(20);
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                new NewTenantMember(s.Tenant, s.Person, [s.ViewerRole], AccessEndsAt: directEnds) { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);
        var sessions = SessionsOf(s, link, made.Credential);

        (await TenantService(Contexts(s.Tenant, s.Manager), sessions).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await AccessEndAsync(s)).Should().Be(teamEnds);
        (await LinkStateAsync(s, link)).Revoked.Should().BeTrue("a guest link made under the direct access goes with it");
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        IsCached(sessions, s, made.Credential).Should().BeFalse();
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task Removing_a_direct_reason_with_an_end_beside_an_unending_teams_caps_nothing_the_person_made()
    {
        var s = await SeedAsync("remove-ended-unending-team");
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                new NewTenantMember(s.Tenant, s.Person, [s.ViewerRole], AccessEndsAt: Now.UtcDateTime.AddDays(10))
                    { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);

        (await TenantService(Contexts(s.Tenant, s.Manager)).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await AccessEndAsync(s)).Should().BeNull();
        (await LinkStateAsync(s, link)).Revoked.Should().BeTrue("a guest link made under the direct access goes with it");
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)null, false, (DateTime?)null));
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task Removing_a_direct_reason_with_no_end_beside_an_unending_teams_caps_nothing_the_person_made()
    {
        var s = await SeedAsync("remove-unending-team");
        await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.ViewerRole]);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic");
        var made = await MadeByPersonAsync(s);

        (await TenantService(Contexts(s.Tenant, s.Manager)).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await AccessEndAsync(s)).Should().BeNull();
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)null, false, (DateTime?)null));
    }

    [Fact]
    public async Task Ending_a_teams_reason_caps_what_the_person_made_at_the_end_of_the_direct_reason_left()
    {
        var s = await SeedAsync("team-end-caps");
        var directEnds = Now.UtcDateTime.AddDays(10);
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                new NewTenantMember(s.Tenant, s.Person, [s.ViewerRole], AccessEndsAt: directEnds) { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        var access = await AccessOfTeamAsync(s, await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic"));
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);
        (await AccessEndAsync(s)).Should().BeNull();

        IReadOnlyList<OAuthGrantRecord>? lowered;
        await using (var unit = DbUnitOfWork.Own(Context(s.Tenant, null)))
        {
            lowered = await unit.CommitAsync(async token =>
            {
                var capped = await TenantMembershipRepository.StageRemoveTeamReasonAsync(
                    unit, s.Tenant, s.Person, access, MemberAccessSource.System, Now.UtcDateTime, token);
                await unit.Context.SaveChangesAsync(token);
                return capped;
            }, ct: Ct);
        }

        lowered.Should().NotBeNull();
        lowered!.Select(g => g.Id).Should().BeEquivalentTo([link, made.Credential]);
        (await AccessEndAsync(s)).Should().Be(directEnds);
        (await LinkStateAsync(s, link)).Should().Be(((DateTime?)directEnds, false));
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)directEnds, false, (DateTime?)directEnds));
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task Ending_a_teams_reason_on_a_row_another_build_cleared_caps_at_the_end_its_alignment_gives_back()
    {
        var s = await SeedAsync("team-end-cleared");
        var teamEnds = Now.UtcDateTime.AddDays(20);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        var access = await AccessOfTeamAsync(s, await TeamReasonAsync(s.Tenant, s.Person, s.ViewerRole, "Camp", endsAt: teamEnds.AddDays(-5)));
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);
        await ClearAccessEndAsync(s);

        IReadOnlyList<OAuthGrantRecord>? lowered;
        await using (var unit = DbUnitOfWork.Own(Context(s.Tenant, null)))
        {
            lowered = await unit.CommitAsync(async token =>
            {
                var capped = await TenantMembershipRepository.StageRemoveTeamReasonAsync(
                    unit, s.Tenant, s.Person, access, MemberAccessSource.System, Now.UtcDateTime, token);
                await unit.Context.SaveChangesAsync(token);
                return capped;
            }, ct: Ct);
        }

        lowered!.Select(g => g.Id).Should().BeEquivalentTo([link, made.Credential]);
        (await AccessEndAsync(s)).Should().Be(teamEnds);
        (await LinkStateAsync(s, link)).Should().Be(((DateTime?)teamEnds, false));
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task The_startup_check_caps_what_the_person_made_at_the_end_it_gives_back_to_a_row_another_build_cleared()
    {
        var s = await SeedAsync("reconcile-cleared");
        var teamEnds = Now.UtcDateTime.AddDays(20);
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);
        await ClearAccessEndAsync(s);

        await ReconcileAsync();

        (await AccessEndAsync(s)).Should().Be(teamEnds);
        (await LinkStateAsync(s, link)).Should().Be(((DateTime?)teamEnds, false));
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        await RowIsTheSumAsync(s, s.Person);
    }

    [Fact]
    public async Task The_startup_check_caps_each_cleared_row_under_its_own_tenant()
    {
        var (first, second) = await TwoSitesAsync("reconcile-two");
        var teamEnds = Now.UtcDateTime.AddDays(20);
        var firstMade = await TeamOnlyWithMadeAsync(first, teamEnds);
        var secondMade = await TeamOnlyWithMadeAsync(second, teamEnds);
        await ClearAccessEndAsync(first);
        await ClearAccessEndAsync(second);

        await ReconcileAsync();

        foreach (var (s, (link, made)) in new[] { (first, firstMade), (second, secondMade) })
        {
            (await AccessEndAsync(s)).Should().Be(teamEnds);
            (await LinkStateAsync(s, link)).Should().Be(((DateTime?)teamEnds, false));
            (await MadeStateAsync(s, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        }
    }

    [Fact]
    public async Task The_startup_check_carries_on_past_a_member_whose_cap_fails()
    {
        var (first, second) = await TwoSitesAsync("reconcile-fails");
        var teamEnds = Now.UtcDateTime.AddDays(20);
        var (_, broken) = await TeamOnlyWithMadeAsync(first, teamEnds);
        var (link, made) = await TeamOnlyWithMadeAsync(second, teamEnds);
        await SetChannelTypeAsync(first, broken.Channel, "not_a_channel");
        await ClearAccessEndAsync(first);
        await ClearAccessEndAsync(second);

        try
        {
            await ReconcileAsync();

            (await AccessEndAsync(first)).Should().BeNull("the failed member's transaction rolled back");
            (await AccessEndAsync(second)).Should().Be(teamEnds);
            (await LinkStateAsync(second, link)).Should().Be(((DateTime?)teamEnds, false));
            (await MadeStateAsync(second, made)).Should().Be(((DateTime?)teamEnds, false, (DateTime?)teamEnds));
        }
        finally
        {
            await SetChannelTypeAsync(first, broken.Channel, "in_app");
        }
    }

    [Fact]
    public async Task The_lapse_sweep_moves_no_end_and_caps_nothing_the_person_made()
    {
        var s = await SeedAsync("lapse-caps-nothing");
        var directEnds = Now.UtcDateTime.AddDays(1);
        var teamEnds = Now.UtcDateTime.AddDays(20);
        (await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                new NewTenantMember(s.Tenant, s.Person, [s.ViewerRole], AccessEndsAt: directEnds) { How = MembershipJoinHow.Direct, By = null },
                Ct))
            .Should().BeTrue();
        await TeamReasonAsync(s.Tenant, s.Person, s.CaretakerRole, "Clinic", endsAt: teamEnds);
        var link = await GuestLinkByPersonAsync(s);
        var made = await MadeByPersonAsync(s);
        (await AccessEndAsync(s)).Should().Be(teamEnds);

        await new MembershipReasonLapses(Contexts(s.Tenant), NullLogger<MembershipReasonLapses>.Instance)
            .NarrowLapsedAsync(directEnds.AddMinutes(1), Ct);

        (await ReadInTenantAsync(s.Tenant, $"""
            SELECT coalesce(next_reason_lapse_at::text, 'none') FROM tenant_members
             WHERE tenant_id = '{s.Tenant}' AND subject_id = '{s.Person}'
            """)).Should().Equal(["none"], "the sweep recomputed the row");
        (await AccessEndAsync(s)).Should().Be(teamEnds);
        (await LinkStateAsync(s, link)).Should().Be(((DateTime?)null, false));
        (await MadeStateAsync(s, made)).Should().Be(((DateTime?)null, false, (DateTime?)null));
    }

    private Task<List<string>> ReadAsync(string sql) => ReadInTenantAsync(null, sql);

    private async Task<List<string>> ReadInTenantAsync(Guid? tenant, string sql)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        if (tenant is { } pinned)
        {
            await using var pin = new NpgsqlCommand($"SELECT set_config('app.current_tenant_id', '{pinned}', false)", conn);
            await pin.ExecuteNonQueryAsync();
        }

        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
