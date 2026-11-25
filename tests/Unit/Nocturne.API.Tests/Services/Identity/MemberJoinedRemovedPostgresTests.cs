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
/// Every way a membership starts or ends, on PostgreSQL under the runtime role, leaves one
/// <c>member_joined</c> or <c>member_removed</c> row: first among a join's rows and last among a
/// removal's, naming how, the invite and the subject responsible, with the actor each path already used.
/// </summary>
[Trait("Category", "Integration")]
public class MemberJoinedRemovedPostgresTests(MemberJoinedRemovedPostgresTests.Database database)
    : IClassFixture<MemberJoinedRemovedPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("member_joined_removed");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Row(string EventType, string? Details, Guid? Actor, string? Credential, Guid? Target, Guid? Tenant)
    {
        public string? How => Read("how");
        public string? Invite => Read("invite");
        public string? By => Read("by");

        private string? Read(string key)
        {
            if (Details is null)
                return null;
            using var document = JsonDocument.Parse(Details);
            return document.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
        }
    }

    private sealed record Seeded(Guid Tenant, Guid Manager, Guid Person, Guid CaretakerRole, Guid OwnerRole);

    private NocturneDbContext Context(Guid tenant, Guid? actor)
    {
        var context = TestContexts.Context(database.AppConnectionString)
            .ActingAs(actor is { } subject ? new TestAuditActor { SubjectId = subject } : null);
        context.TenantId = tenant;
        return context;
    }

    private SuppliedContextFactory Contexts(Guid tenant, Guid? actor = null) => new(() => Context(tenant, actor));

    private async Task<Seeded> SeedAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'joined-removed-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var contexts = Contexts(tenant);
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, Ct);
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        var owner = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var subjects = new SubjectRepository(contexts, AccountLinkRoutes.Off);
        var manager = await subjects.CreateAsync(new NewSubject($"{slug}-manager"), Ct);
        var person = await subjects.CreateAsync(new NewSubject($"{slug}-person"), Ct);
        (await members.AddMemberAsync(new NewTenantMember(tenant, manager, [owner]) { How = MembershipJoinHow.Direct, By = null }, Ct)).Should().BeTrue();
        return new Seeded(tenant, manager, person, caretaker, owner);
    }

    private async Task<List<Row>> RowsAsync(Guid tenant, Guid target)
    {
        await using var db = Context(Guid.Empty, null);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(a => a.TenantId == tenant && a.TargetSubjectId == target)
                .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                .ToListAsync())
            .Select(a => new Row(a.EventType, a.DetailsJson, a.ActorSubjectId, a.ActorCredential, a.TargetSubjectId, a.TenantId))
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

    private static TenantService TenantService(SuppliedContextFactory contexts) => new(
        contexts,
        new TenantRepository(contexts),
        new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
        new SubjectRepository(contexts, AccountLinkRoutes.Off),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        Revocation(),
        new FakeTimeProvider(Now),
        Mock.Of<ILogger<TenantService>>(),
        AccountLinkRoutes.Off, InertTeamStaff.Announcer());

    private async Task<(Guid Id, string Token)> InviteAsync(Seeded s, string? label, DateTime? accessEndsAt = null)
    {
        var token = $"token-{Guid.NewGuid():N}";
        var id = await new MemberInviteRepository(Contexts(s.Tenant)).CreateAsync(
            new NewMemberInvite(
                s.Tenant, s.Manager, HashUtils.Sha256Hex(token), [s.CaretakerRole], null, label, false,
                Now.UtcDateTime.AddDays(7), null, Now.UtcDateTime, accessEndsAt),
            Ct);
        return (id, token);
    }

    /// <summary>A live guest link the person made on the manager's data, which removing the person revokes.</summary>
    private async Task GuestLinkAsync(Seeded s) =>
        await new OAuthGrantRepository(Contexts(s.Tenant)).CreateAsync(
            new NewOAuthGrant(
                null, s.Manager, OAuthGrantTypes.Guest, [Scope.GlucoseRead], "link", Guid.NewGuid().ToString("N"), null,
                Now.UtcDateTime.AddDays(2), false, CreatedBySubjectId: s.Person),
            Ct);

    [Fact]
    public async Task AcceptingAnInviteWritesTheJoinedRowFirstWithTheInviteItsMakerAndTheJoinerAsTheActor()
    {
        var s = await SeedAsync("invite");
        var (invite, token) = await InviteAsync(s, "Family");

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Person, s.Tenant)).Success.Should().BeTrue();

        var rows = await RowsAsync(s.Tenant, s.Person);
        rows.Select(r => r.EventType).Should().Equal(AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned);
        rows[0].Should().Match<Row>(r => r.How == "invite" && r.Invite == invite.ToString() && r.By == s.Manager.ToString()
            && r.Actor == s.Person && r.Target == s.Person);
    }

    [Fact]
    public async Task ApprovingAMembershipRequestNamesTheApproverAsByAndAsTheActor()
    {
        var s = await SeedAsync("request");
        var members = new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off);
        var request = await members.CreateRequestAsync(new NewMembershipRequest(s.Tenant, s.Person, null, Now.UtcDateTime), Ct);
        var contexts = Contexts(s.Tenant, s.Manager);
        var service = new MembershipRequestService(
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

        (await service.ApproveRequestAsync(request, s.Tenant, [s.CaretakerRole], s.Manager, [Scope.FullAccess], accessEndsAt: null))
            .Error.Should().BeNull();

        var rows = await RowsAsync(s.Tenant, s.Person);
        rows.Select(r => r.EventType).Should().Equal(AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned);
        rows[0].Should().Match<Row>(r => r.How == "request" && r.Invite == null && r.By == s.Manager.ToString()
            && r.Actor == s.Manager && r.Target == s.Person);
    }

    [Fact]
    public async Task ApprovingAnAccessRequestIsAJoinByRequestNamingTheApprover()
    {
        var s = await SeedAsync("access");
        var pending = await new SubjectRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).CreateAsync(
            new NewSubject("access-pending", IsActive: false, ApprovalStatus: "Pending"), Ct);
        var contexts = Contexts(s.Tenant, s.Manager);
        var service = new AccessRequestService(
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            contexts,
            GrantsEverything(),
            Mock.Of<IInAppNotificationService>(),
            new FakeTimeProvider(Now),
            NullLogger<AccessRequestService>.Instance,
            AccountLinkRoutes.Off,
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))), InertTeamStaff.Announcer());

        (await service.ApproveAsync(pending, s.Tenant, [s.CaretakerRole], null, false, [Scope.FullAccess], s.Manager))
            .Outcome.Should().Be(ApproveAccessRequestOutcome.Approved);

        var rows = await RowsAsync(s.Tenant, pending);
        rows[0].Should().Match<Row>(r => r.EventType == AuthAuditEventType.MemberJoined && r.How == "request"
            && r.By == s.Manager.ToString() && r.Actor == s.Manager);
    }

    [Fact]
    public async Task AddingAMemberDirectlyIsAJoinByTheActorWithNoBy()
    {
        var s = await SeedAsync("direct");

        (await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole])).Should().BeTrue();

        var rows = await RowsAsync(s.Tenant, s.Person);
        rows.Select(r => r.EventType).Should().Equal(AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned);
        rows[0].Should().Match<Row>(r => r.How == "direct" && r.By == null && r.Invite == null && r.Actor == s.Manager);
    }

    [Fact]
    public async Task ATenantCreatedWithItsOwnerJoinsThePublicSubjectAsTheSystemAndTheOwnerAsSetup()
    {
        var subjects = new SubjectRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off);
        var owner = await subjects.CreateAsync(new NewSubject("setup-owner"), Ct);
        var publicSubject = (await subjects.FindSystemSubjectAsync(PublicSubjectFilter.PublicSubjectName, Ct))?.Id
            ?? await subjects.CreateAsync(new NewSubject(PublicSubjectFilter.PublicSubjectName, IsSystemSubject: true), Ct);

        var created = await TenantService(Contexts(Guid.Empty)).CreateWithOwnerAsync($"setup-{Guid.NewGuid():N}"[..20], "Setup", owner);

        var ownerRows = await RowsAsync(created.Id, owner);
        ownerRows.Select(r => r.EventType).Should().Equal(AuthAuditEventType.MemberJoined, AuthAuditEventType.RoleAssigned);
        ownerRows[0].Should().Match<Row>(r => r.How == "setup" && r.Actor == owner && r.By == null);
        (await RowsAsync(created.Id, publicSubject)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberJoined)
            .Which.Should().Match<Row>(r => r.How == "system" && r.Actor == null && r.Credential == "system");
    }

    [Fact]
    public async Task AManagerRemovingAMemberWritesTheRemovedRowLastWithTheManagerAsTheActor()
    {
        var s = await SeedAsync("removed");
        await TenantService(Contexts(s.Tenant)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole], [Scope.TreatmentsRead]);
        await GuestLinkAsync(s);

        (await TenantService(Contexts(s.Tenant, s.Manager)).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        var rows = (await RowsAsync(s.Tenant, s.Person)).Skip(3).ToList();
        rows.Select(r => r.EventType).Should().Equal(
            AuthAuditEventType.GuestLinkRevoked, AuthAuditEventType.RoleRemoved, AuthAuditEventType.RoleRemoved,
            AuthAuditEventType.MemberRemoved);
        rows[^1].Should().Match<Row>(r => r.How == "removed" && r.Actor == s.Manager && r.Target == s.Person);
    }

    [Fact]
    public async Task AMemberWhoRemovesTheirOwnMembershipLeft()
    {
        var s = await SeedAsync("left");
        await TenantService(Contexts(s.Tenant)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole]);

        (await TenantService(Contexts(s.Tenant, s.Person)).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await RowsAsync(s.Tenant, s.Person))[^1].Should().Match<Row>(r =>
            r.EventType == AuthAuditEventType.MemberRemoved && r.How == "left" && r.Actor == s.Person);
    }

    [Fact]
    public async Task AMemberWithNoRoleAndNoPermissionStillLeavesAJoinedAndARemovedRow()
    {
        var s = await SeedAsync("bare");

        (await TenantService(Contexts(s.Tenant, s.Manager)).AddMemberAsync(s.Tenant, s.Person, [])).Should().BeTrue();
        (await TenantService(Contexts(s.Tenant, s.Manager)).RemoveMemberAsync(s.Tenant, s.Person)).Ok.Should().BeTrue();

        (await RowsAsync(s.Tenant, s.Person)).Select(r => (r.EventType, r.How)).Should().Equal(
            (AuthAuditEventType.MemberJoined, "direct"), (AuthAuditEventType.MemberRemoved, "removed"));
    }

    [Fact]
    public async Task JoiningAgainWritesTheReplacedRowThenTheJoinedRowInOneCommit()
    {
        var s = await SeedAsync("rejoin");
        await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
            new NewTenantMember(s.Tenant, s.Person, [s.CaretakerRole], AccessEndsAt: Now.UtcDateTime.AddDays(-1)) { How = MembershipJoinHow.Direct, By = null }, Ct);
        await GuestLinkAsync(s);
        var before = (await RowsAsync(s.Tenant, s.Person)).Count;
        var (invite, token) = await InviteAsync(s, label: null);

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Person, s.Tenant)).Success.Should().BeTrue();

        var rows = (await RowsAsync(s.Tenant, s.Person)).Skip(before).ToList();
        rows.Select(r => (r.EventType, r.How)).Should().Equal(
            (AuthAuditEventType.GuestLinkRevoked, null),
            (AuthAuditEventType.RoleRemoved, null),
            (AuthAuditEventType.MemberRemoved, "replaced"),
            (AuthAuditEventType.MemberJoined, "invite"),
            (AuthAuditEventType.RoleAssigned, null));
        rows[3].Invite.Should().Be(invite.ToString());
    }

    [Fact]
    public async Task AJoinRefusedAfterTheReplaceLeavesNeitherRow()
    {
        var s = await SeedAsync("rejoin-refused");
        await new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
            new NewTenantMember(s.Tenant, s.Person, [s.CaretakerRole], AccessEndsAt: Now.UtcDateTime.AddDays(-1)) { How = MembershipJoinHow.Direct, By = null }, Ct);
        var before = await RowsAsync(s.Tenant, s.Person);
        var token = $"token-{Guid.NewGuid():N}";
        var invites = new MemberInviteRepository(Contexts(s.Tenant));
        var invite = await invites.CreateAsync(
            new NewMemberInvite(s.Tenant, s.Manager, HashUtils.Sha256Hex(token), [s.CaretakerRole], null, null, false,
                Now.UtcDateTime.AddDays(7), 1, Now.UtcDateTime, null), Ct);
        (await invites.TryClaimUseAsync(invite, Now.UtcDateTime, Ct)).Should().Be(MemberInviteClaim.Claimed);
        var contexts = Contexts(s.Tenant);
        var service = new MemberInviteService(
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new StaleInviteReads(contexts),
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            contexts,
            Mock.Of<IJwtService>(),
            Mock.Of<ITenantRoleService>(),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
            new FakeTimeProvider(Now),
            NullLogger<MemberInviteService>.Instance,
            AccountLinkRoutes.Off,
            Revocation(), InertTeamStaff.Announcer());

        (await service.AcceptInviteAsync(token, s.Person, s.Tenant)).ErrorCode.Should().Be("exhausted");

        (await RowsAsync(s.Tenant, s.Person)).Should().BeEquivalentTo(before, o => o.WithStrictOrdering());
    }

    /// <summary>Reads every invite as unused, as an accept that read it before another took its last use does.</summary>
    private sealed class StaleInviteReads(INocturneDbContextFactory contexts) : MemberInviteRepository(contexts)
    {
        public override async Task<MemberInviteRecord?> FindByTokenHashAsync(string tokenHash, Guid tenantId, CancellationToken ct) =>
            await base.FindByTokenHashAsync(tokenHash, tenantId, ct) is { } invite ? invite with { UseCount = 0 } : null;
    }

    [Fact]
    public async Task DeletingAnAccountWritesARemovedRowPerMembershipWhoseDetailsTheScrubClears()
    {
        var s = await SeedAsync("deleted");
        var other = await SeedAsync("deleted-other");
        await TenantService(Contexts(s.Tenant)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole]);
        await TenantService(Contexts(other.Tenant)).AddMemberAsync(other.Tenant, s.Person, []);

        (await new SubjectRepository(Contexts(s.Tenant, s.Manager), AccountLinkRoutes.Off).DeleteAsync(s.Person, Ct))
            .Should().BeOfType<SubjectChange.Changed>();

        foreach (var tenant in new[] { s.Tenant, other.Tenant })
        {
            var removed = (await RowsAsync(tenant, s.Person)).Where(r => r.EventType == AuthAuditEventType.MemberRemoved).ToList();
            removed.Should().ContainSingle().Which.Should().Match<Row>(r =>
                r.Details == null && r.Actor == s.Manager && r.Target == s.Person && r.Tenant == tenant);
        }

        (await RowsAsync(s.Tenant, s.Person)).Should().NotContain(r => r.EventType == AuthAuditEventType.RoleRemoved,
            "the deletion removes the membership and records only that it ended");
        await using var db = Context(Guid.Empty, null);
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == s.Person)).Should().BeFalse();

        var history = await new MemberHistoryReader(Contexts(s.Tenant)).GetAsync(s.Tenant, s.Person, 25, 0, Now.UtcDateTime, Ct);
        history.Data.First().Should().Match<MemberHistoryEntry>(e => e.Kind == MemberHistoryKind.Removed
            && e.RemovedHow == null && e.Member == new MemberHistoryPerson(s.Person, null));
    }

    [Fact]
    public async Task AnAccountDeletionRefusedForTheLastOwnerWritesNoRemovedRow()
    {
        var s = await SeedAsync("deleted-owner");

        (await new SubjectRepository(Contexts(s.Tenant, s.Manager), AccountLinkRoutes.Off).DeleteAsync(s.Manager, Ct))
            .Should().BeOfType<SubjectChange.LastOwnerOf>();

        (await RowsAsync(s.Tenant, s.Manager)).Should().NotContain(r => r.EventType == AuthAuditEventType.MemberRemoved);
    }

    [Fact]
    public async Task TheDemoResetRemovesAsTheSystem()
    {
        var s = await SeedAsync("demo");
        await TenantService(Contexts(s.Tenant)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole]);

        await new TenantMembershipRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off).RemoveMembershipsOfSubjectAsync(s.Person, Ct);

        (await RowsAsync(s.Tenant, s.Person))[^1].Should().Match<Row>(r =>
            r.EventType == AuthAuditEventType.MemberRemoved && r.How == "system" && r.Credential == "system");
    }

    [Fact]
    public async Task SetupAdoptionEndsTheCreatedSubjectsMembershipAndStartsTheHoldersWithTheSameAccess()
    {
        var s = await SeedAsync("adopt");
        var contexts = Contexts(s.Tenant);
        var subjects = new SubjectRepository(contexts, AccountLinkRoutes.Off);
        var (invite, _) = await InviteAsync(s, "Setup");
        var end = Now.UtcDateTime.AddDays(30);
        var created = await subjects.CreateSetupOwnerAsync(s.Tenant, new NewSubject("Owner Name", Username: "owner"), Ct);
        (await new TenantMembershipRepository(contexts, AccountLinkRoutes.Off).AddMemberAsync(
            new NewTenantMember(
                s.Tenant, created, [s.OwnerRole], [Scope.GlucoseRead], Label: "Owner", Username: "owner", LimitTo24Hours: true,
                CreatedFromInviteId: invite, MadeBy: MembershipMadeBy.Member, AccessEndsAt: end)
            {
                How = MembershipJoinHow.Setup,
                By = null,
            },
            Ct)).Should().BeTrue();
        var holder = await subjects.CreateAsync(new NewSubject("Holder"), Ct);
        var provider = Guid.NewGuid();
        await using (var db = Context(s.Tenant, null))
        {
            db.OidcProviders.Add(new OidcProviderEntity
            {
                Id = provider, Name = "Keycloak", IssuerUrl = "https://issuer.example", ClientId = "nocturne", IsEnabled = true,
            });
            await db.SaveChangesAsync();
        }
        var identity = await subjects.AddOidcIdentityAsync(
            new NewSubjectOidcIdentity(holder, provider, $"ext-{Guid.NewGuid():N}", "https://issuer.example", null, Now.UtcDateTime), Ct);

        (await subjects.AdoptForSetupAsync(created, holder, identity, (await new TenantRepository(contexts).GetAsync(s.Tenant, Ct))!.Slug, Ct))
            .Should().BeTrue();

        var membership = (await new TenantMembershipRepository(contexts, AccountLinkRoutes.Off).FindMemberAsync(s.Tenant, holder, Ct))!;
        membership.Roles.Select(r => r.RoleId).Should().Equal(s.OwnerRole);
        membership.Should().BeEquivalentTo(new
        {
            DirectPermissions = new[] { Scope.GlucoseRead },
            LimitTo24Hours = true,
            AccessEndsAt = (DateTime?)end,
            Label = "Owner",
            Username = "owner",
        });
        await using (var db = Context(s.Tenant, null))
        {
            (await db.TenantMembers.SingleAsync(m => m.SubjectId == holder)).CreatedFromInviteId.Should().Be(invite);
            (await db.TenantMembers.AnyAsync(m => m.SubjectId == created)).Should().BeFalse();
        }

        var createdRows = await RowsAsync(s.Tenant, created);
        createdRows[^1].Should().Match<Row>(r => r.EventType == AuthAuditEventType.MemberRemoved && r.How == "system");
        (await RowsAsync(s.Tenant, holder)).Concat(createdRows).Should().NotContain(r => r.How == "replaced",
            "nobody joined again; the membership passed to another subject");
        var history = await new MemberHistoryReader(contexts).GetAsync(s.Tenant, holder, 25, 0, Now.UtcDateTime, Ct);
        history.Data.Last().Should().Match<MemberHistoryEntry>(e => e.Kind == MemberHistoryKind.Joined
            && e.JoinedHow == MembershipJoinHow.Setup && e.Actor.SubjectId == holder);
        history.Data.Should().Contain(e => e.Kind == MemberHistoryKind.RoleGiven && e.Role == RoleSeeds.Owner);
    }

    [Fact]
    public async Task ASubjectWhoDeletesTheirOwnAccountKeepsTheDetailsOfTheTeamItDeletes()
    {
        var s = await SeedAsync("team-delete");
        await TenantService(Contexts(s.Tenant)).AddMemberAsync(s.Tenant, s.Person, [s.CaretakerRole]);
        var team = await new TeamRepository(Contexts(s.Tenant)).CreateAsync(s.Person, "Night shift", Ct);

        (await new SubjectRepository(Contexts(s.Tenant, s.Person), AccountLinkRoutes.Off).DeleteAsync(s.Person, Ct))
            .Should().BeOfType<SubjectChange.Changed>();

        await using var db = Context(Guid.Empty, null);
        var deleted = await db.AuthAuditLog.AsNoTracking()
            .SingleAsync(a => a.TeamId == team.Id && a.EventType == AuthAuditEventType.TeamDeleted);
        deleted.DetailsJson.Should().Contain("Night shift");
        (await RowsAsync(s.Tenant, s.Person)).Should().ContainSingle(r => r.EventType == AuthAuditEventType.MemberRemoved)
            .Which.Details.Should().BeNull();
        var history = await new MemberHistoryReader(Contexts(s.Tenant)).GetAsync(s.Tenant, null, 50, 0, Now.UtcDateTime, Ct);
        history.Data.Should().NotContain(e => e.Team != null,
            "a team's own rows name no site, and the team never had access to this one");
    }
}
