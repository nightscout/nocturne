using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.API.Configuration;
using Nocturne.API.Multitenancy;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Chat;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// An ended membership on PostgreSQL under the runtime role and Row Level Security: a subject who
/// joins again gets a new membership in place of the ended one, in one commit, without meeting the
/// unique index on the tenant and subject; and the chat directory the bot answers through, whose
/// links are read on a platform context and each held to a live membership read pinned to the link's
/// tenant, stops naming the tenant once the membership ends and names it again when the end moves
/// later.
/// </summary>
[Trait("Category", "Integration")]
public class MembershipAccessEndPostgresTests(MembershipAccessEndPostgresTests.Database database)
    : IClassFixture<MembershipAccessEndPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("membership_access_end");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private static GrantRevocationService Revocation() =>
        new(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())));

    private sealed record Seeded(Guid Tenant, Guid Guest, Guid CaretakerRole, SuppliedContextFactory Contexts);

    /// <summary>A tenant with its seed roles and a caretaker whose membership ends at <paramref name="accessEndsAt"/>.</summary>
    private async Task<Seeded> SeedAsync(string slug, DateTime? accessEndsAt)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'access-end-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, CancellationToken.None);
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        var guest = await new SubjectRepository(contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-guest"), CancellationToken.None);
        (await members.AddMemberAsync(new NewTenantMember(tenant, guest, [caretaker], AccessEndsAt: accessEndsAt) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None))
            .Should().BeTrue();
        return new Seeded(tenant, guest, caretaker, contexts);
    }

    private async Task<List<(DateTime? AccessEndsAt, Guid Id)>> MembershipRowsAsync(Seeded seeded)
    {
        await using var db = Context(seeded.Tenant);
        return (await db.TenantMembers.AsNoTracking()
                .Where(m => m.TenantId == seeded.Tenant && m.SubjectId == seeded.Guest)
                .Select(m => new { m.AccessEndsAt, m.Id })
                .ToListAsync())
            .Select(m => (m.AccessEndsAt, m.Id))
            .ToList();
    }

    [Fact]
    public async Task JoiningAgainReplacesTheEndedMembershipInOneCommitAndLeavesOneRow()
    {
        var seeded = await SeedAsync("rejoin", Now.UtcDateTime.AddDays(-1));
        var ended = (await MembershipRowsAsync(seeded)).Single();
        var grantHash = Guid.NewGuid().ToString("N");
        var grant = await new OAuthGrantRepository(seeded.Contexts).CreateAsync(
            new NewOAuthGrant(null, seeded.Guest, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "old uploader", grantHash, null, null, false),
            CancellationToken.None);

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(seeded.Contexts, seeded.Tenant))
        {
            var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
            await unit.CommitAsync(async ct =>
            {
                (await MembershipRemoval.ReplaceEndedAsync(
                    unit, members, Revocation(), seeded.Tenant, seeded.Guest, Now.UtcDateTime, ct)).Should().ContainSingle(g => g.Id == grant);
                (await members.AddMemberAsync(new NewTenantMember(seeded.Tenant, seeded.Guest, [seeded.CaretakerRole]) { How = MembershipJoinHow.Direct, By = null }, ct)).Should().BeTrue();
                await unit.Context.SaveChangesAsync(ct);
            });
        }

        var rows = await MembershipRowsAsync(seeded);
        rows.Should().ContainSingle().Which.Should().Match<(DateTime? AccessEndsAt, Guid Id)>(
            row => row.AccessEndsAt == null && row.Id != ended.Id);
        (await new OAuthGrantRepository(seeded.Contexts).GetStateAsync(seeded.Tenant, grant, CancellationToken.None))!
            .RevokedAt.Should().NotBeNull("the ended membership went through the removal path");
    }

    [Fact]
    public async Task JoiningAgainIsRefusedWhileTheMembershipHasNotEnded()
    {
        var seeded = await SeedAsync("live", Now.UtcDateTime.AddDays(1));

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(seeded.Contexts, seeded.Tenant))
        {
            var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
            var replaced = await unit.CommitAsync(ct =>
                MembershipRemoval.ReplaceEndedAsync(unit, members, Revocation(), seeded.Tenant, seeded.Guest, Now.UtcDateTime, ct));
            replaced.Should().BeNull();
        }

        (await MembershipRowsAsync(seeded)).Should().ContainSingle().Which.AccessEndsAt.Should().Be(Now.UtcDateTime.AddDays(1));
    }

    [Fact]
    public async Task TheBotStopsResolvingAChatLinkWhenItsMembershipEndsAndResolvesItAgainWhenTheEndMovesLater()
    {
        var clock = new FakeTimeProvider(Now);
        var seeded = await SeedAsync("chat", Now.UtcDateTime.AddMinutes(1));
        var other = await SeedAsync("chat-other", accessEndsAt: null);
        var platform = new SuppliedContextFactory(() => Context(Guid.Empty));
        var directory = new ChatIdentityDirectoryService(platform, new TenantMembershipRepository(platform, AccountLinkRoutes.Off), clock, Mock.Of<ILogger<ChatIdentityDirectoryService>>());
        var platformUser = $"discord-{Guid.NewGuid():N}";
        await directory.CreateLinkAsync("discord", platformUser, seeded.Tenant, seeded.Guest, "ending", "Ending", CancellationToken.None);
        await directory.CreateLinkAsync("discord", platformUser, other.Tenant, other.Guest, "staying", "Staying", CancellationToken.None);

        (await directory.GetCandidatesAsync("discord", platformUser, CancellationToken.None))
            .Select(l => l.Label).Should().BeEquivalentTo(["ending", "staying"]);
        (await directory.GetDefaultAsync("discord", platformUser, CancellationToken.None))!.Label.Should().Be("ending");

        clock.Advance(TimeSpan.FromMinutes(1));
        (await directory.GetCandidatesAsync("discord", platformUser, CancellationToken.None))
            .Select(l => l.Label).Should().Equal("staying");
        (await directory.GetDefaultAsync("discord", platformUser, CancellationToken.None)).Should().BeNull(
            "the default link's membership has ended, and the other link is not the default");
        (await directory.GetByTenantAsync(seeded.Tenant, CancellationToken.None))
            .Should().ContainSingle("the link is kept for the settings list, so a later end restores it");

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(
            seeded.Contexts, AccountLinkRoutes.Off, seeded.Tenant, (await MembershipRowsAsync(seeded)).Single().Id,
            Now.UtcDateTime.AddDays(7), clock.GetUtcNow().UtcDateTime, CancellationToken.None)).Should().Be(OwnerGuardedChange.Changed);
        (await directory.GetCandidatesAsync("discord", platformUser, CancellationToken.None))
            .Select(l => l.Label).Should().BeEquivalentTo(["ending", "staying"]);
    }

    /// <summary>An ended membership with a live grant and a chat link of its member, as a refused rejoin must leave it.</summary>
    private sealed record EndedState(Seeded Seeded, Guid MemberId, Guid Grant, Guid ChatLink);

    private async Task<EndedState> SeedEndedWithCredentialsAsync(string slug)
    {
        var seeded = await SeedAsync(slug, Now.UtcDateTime.AddDays(-1));
        var memberId = (await MembershipRowsAsync(seeded)).Single().Id;
        var grant = await new OAuthGrantRepository(seeded.Contexts).CreateAsync(
            new NewOAuthGrant(null, seeded.Guest, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "uploader", Guid.NewGuid().ToString("N"), null, null, false),
            CancellationToken.None);
        var platform = new SuppliedContextFactory(() => Context(Guid.Empty));
        var link = await new ChatIdentityDirectoryService(
                platform, new TenantMembershipRepository(platform, AccountLinkRoutes.Off), new FakeTimeProvider(Now),
                Mock.Of<ILogger<ChatIdentityDirectoryService>>())
            .CreateLinkAsync("discord", $"discord-{Guid.NewGuid():N}", seeded.Tenant, seeded.Guest, slug, slug, CancellationToken.None);
        return new EndedState(seeded, memberId, grant, link.Id);
    }

    private async Task ShouldBeUntouchedAsync(EndedState state)
    {
        (await MembershipRowsAsync(state.Seeded)).Should().ContainSingle()
            .Which.Should().Be((Now.UtcDateTime.AddDays(-1), state.MemberId), "the ended row stays as it was");
        (await new OAuthGrantRepository(state.Seeded.Contexts).GetStateAsync(state.Seeded.Tenant, state.Grant, CancellationToken.None))!
            .RevokedAt.Should().BeNull("the member's grant is not revoked");
        await using var platform = Context(Guid.Empty);
        (await platform.ChatIdentityDirectory.AsNoTracking().AnyAsync(d => d.Id == state.ChatLink)).Should().BeTrue("the chat link stays");
        await using var tenant = Context(state.Seeded.Tenant);
        (await tenant.AuthAuditLog.AsNoTracking().AnyAsync(r =>
                r.TargetSubjectId == state.Seeded.Guest && r.EventType == AuthAuditEventType.RoleRemoved))
            .Should().BeFalse("no removal was recorded");
    }

    /// <summary>Reads every invite as unused, as an accept that read it before another took its last use does.</summary>
    private sealed class StaleInviteReads(INocturneDbContextFactory contexts) : MemberInviteRepository(contexts)
    {
        public override async Task<MemberInviteRecord?> FindByTokenHashAsync(string tokenHash, Guid tenantId, CancellationToken ct) =>
            await base.FindByTokenHashAsync(tokenHash, tenantId, ct) is { } invite ? invite with { UseCount = 0 } : null;
    }

    /// <summary>Reads every request as pending, as an approval that read it before another decided it does.</summary>
    private sealed class StaleRequestReads(INocturneDbContextFactory contexts) : TenantMembershipRepository(contexts, AccountLinkRoutes.Off)
    {
        public override async Task<MembershipRequestRecord?> GetRequestAsync(Guid tenantId, Guid requestId, CancellationToken ct) =>
            await base.GetRequestAsync(tenantId, requestId, ct) is { } request ? request with { Status = MembershipRequestRecord.Pending } : null;
    }

    [Fact]
    public async Task AnInviteWhoseLastUseIsTakenMidAcceptLeavesTheEndedMembershipItsGrantsAndItsChatLinks()
    {
        var state = await SeedEndedWithCredentialsAsync("exhausted");
        var tenant = state.Seeded.Tenant;
        var token = $"token-{Guid.NewGuid():N}";
        var invites = new MemberInviteRepository(state.Seeded.Contexts);
        var inviteId = await invites.CreateAsync(
            new NewMemberInvite(
                tenant, await NewOwnerAsync(state.Seeded, "exhausted-owner"), HashUtils.Sha256Hex(token),
                [state.Seeded.CaretakerRole], null, null, false, Now.UtcDateTime.AddDays(7), 1, Now.UtcDateTime, null),
            CancellationToken.None);
        (await invites.TryClaimUseAsync(inviteId, Now.UtcDateTime, CancellationToken.None)).Should().Be(MemberInviteClaim.Claimed);
        var service = new MemberInviteService(
            new TenantMembershipRepository(state.Seeded.Contexts, AccountLinkRoutes.Off),
            new StaleInviteReads(state.Seeded.Contexts),
            new SubjectRepository(state.Seeded.Contexts, AccountLinkRoutes.Off),
            state.Seeded.Contexts,
            Mock.Of<IJwtService>(),
            Mock.Of<ITenantRoleService>(),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
            new FakeTimeProvider(Now),
            NullLogger<MemberInviteService>.Instance,
            AccountLinkRoutes.Off,
            Revocation(), InertTeamStaff.Announcer());

        var result = await service.AcceptInviteAsync(token, state.Seeded.Guest, tenant);

        result.ErrorCode.Should().Be("exhausted");
        await ShouldBeUntouchedAsync(state);
    }

    [Fact]
    public async Task ARequestDecidedMidApprovalLeavesTheEndedMembershipItsGrantsItsChatLinksAndTheRequesterUnapproved()
    {
        var state = await SeedEndedWithCredentialsAsync("decided");
        var tenant = state.Seeded.Tenant;
        var members = new TenantMembershipRepository(state.Seeded.Contexts, AccountLinkRoutes.Off);
        var requestId = await members.CreateRequestAsync(
            new NewMembershipRequest(tenant, state.Seeded.Guest, null, Now.UtcDateTime), CancellationToken.None);
        (await members.DecideRequestAsync(
            tenant, requestId, new MembershipRequestDecision(MembershipRequestRecord.Denied, null, Now.UtcDateTime, null),
            CancellationToken.None)).Should().BeTrue();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE subjects SET approval_status = 'Pending', is_active = false WHERE id = @id";
            cmd.Parameters.Add(new NpgsqlParameter("@id", state.Seeded.Guest));
            await cmd.ExecuteNonQueryAsync();
        }

        var service = new MembershipRequestService(
            new TenantRepository(state.Seeded.Contexts),
            new StaleRequestReads(state.Seeded.Contexts),
            new SubjectRepository(state.Seeded.Contexts, AccountLinkRoutes.Off),
            state.Seeded.Contexts,
            Mock.Of<ITenantRoleService>(r => r.ValidateRoleGrantAsync(
                    It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())
                == Task.FromResult(RoleGrantValidation.Valid)),
            Mock.Of<IInAppNotificationService>(),
            Mock.Of<IAuthAuditService>(),
            new FakeTimeProvider(Now),
            NullLogger<MembershipRequestService>.Instance,
            AccountLinkRoutes.Off,
            Revocation(), InertTeamStaff.Announcer());

        var result = await service.ApproveRequestAsync(
            requestId, tenant, [state.Seeded.CaretakerRole], Guid.NewGuid(), [Scope.FullAccess], accessEndsAt: null);

        result.Error.Should().Be("Request is no longer pending.");
        await ShouldBeUntouchedAsync(state);
        (await new SubjectRepository(state.Seeded.Contexts, AccountLinkRoutes.Off).GetAsync(state.Seeded.Guest, CancellationToken.None))!
            .Should().Match<SubjectRecord>(s => !s.IsActive && s.ApprovalStatus == "Pending", "the activation rolled back with the rest");
    }

    private static readonly string[] GuestScopes = [Scope.GlucoseRead];

    private TenantService Service(INocturneDbContextFactory contexts, GuestSessionCacheService sessions) => new(
        contexts,
        new TenantRepository(contexts),
        new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
        new SubjectRepository(contexts, AccountLinkRoutes.Off),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(sessions),
        new FakeTimeProvider(Now),
        Mock.Of<ILogger<TenantService>>(),
        AccountLinkRoutes.Off, InertTeamStaff.Announcer());

    private static GuestSessionCacheService Sessions() => new(new MemoryCache(new MemoryCacheOptions()));

    private Task<Guid> NewSubjectAsync(Seeded seeded, string name) =>
        new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off).CreateAsync(new NewSubject(name), CancellationToken.None);

    /// <summary>An owner of the tenant, whose membership never ends: a maker whose invites stay acceptable.</summary>
    private async Task<Guid> NewOwnerAsync(Seeded seeded, string name)
    {
        var owner = await NewSubjectAsync(seeded, name);
        var members = new TenantMembershipRepository(seeded.Contexts, AccountLinkRoutes.Off);
        var ownerRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(seeded.Tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None))
            .Should().BeTrue();
        return owner;
    }

    private async Task<Guid> GuestLinkAsync(
        Seeded seeded, Guid createdBy, Guid dataOwner, DateTime expiresAt, GuestSessionCacheService? sessions = null)
    {
        var id = await new OAuthGrantRepository(seeded.Contexts).CreateAsync(
            new NewOAuthGrant(
                null, dataOwner, OAuthGrantTypes.Guest, GuestScopes, "link", Guid.NewGuid().ToString("N"), null, expiresAt, false,
                CreatedBySubjectId: createdBy),
            CancellationToken.None);
        sessions?.Set(seeded.Tenant, id, new GuestSessionInfo(id, seeded.Tenant, dataOwner, GuestScopes, "link", expiresAt, false));
        return id;
    }

    private async Task<OAuthGrantRecord> LinkAsync(Seeded seeded, Guid id) =>
        (await new OAuthGrantRepository(seeded.Contexts).GetAsync(id, CancellationToken.None))!;

    private async Task<List<(string EventType, string Details)>> LinkAuditAsync(Seeded seeded, Guid target)
    {
        await using var db = Context(seeded.Tenant);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(r => r.TargetSubjectId == target
                    && (r.EventType == AuthAuditEventType.GuestLinkCapped || r.EventType == AuthAuditEventType.GuestLinkRevoked))
                .Select(r => new { r.EventType, r.DetailsJson })
                .ToListAsync())
            .Select(r => (r.EventType, r.DetailsJson ?? string.Empty))
            .ToList();
    }

    private async Task<OwnerGuardedChange> SetEndAsync(Seeded seeded, GuestSessionCacheService sessions, DateTime? end) =>
        await Service(seeded.Contexts, sessions).SetMemberAccessEndAsync(
            seeded.Tenant, (await MembershipRowsAsync(seeded)).Single().Id, end, Now.UtcDateTime);

    [Fact]
    public async Task SettingAnEndDateCapsTheLinkThatWouldOutliveItAndLeavesOneThatEndsSooner()
    {
        var seeded = await SeedAsync("cap", accessEndsAt: null);
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "cap-owner");
        var long48 = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(48), sessions);
        var short6 = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(6), sessions);
        var end = Now.UtcDateTime.AddDays(1);

        (await SetEndAsync(seeded, sessions, end)).Should().Be(OwnerGuardedChange.Changed);

        (await MembershipRowsAsync(seeded)).Single().AccessEndsAt.Should().Be(end);
        (await LinkAsync(seeded, long48)).ExpiresAt.Should().Be(end);
        (await LinkAsync(seeded, short6)).ExpiresAt.Should().Be(Now.UtcDateTime.AddHours(6));
        sessions.TryGet(seeded.Tenant, long48, out _).Should().BeFalse("the capped link's cached session is evicted");
        sessions.TryGet(seeded.Tenant, short6, out _).Should().BeTrue("a link that was not changed keeps its session");
        var audit = (await LinkAuditAsync(seeded, seeded.Guest)).Should().ContainSingle().Subject;
        audit.EventType.Should().Be(AuthAuditEventType.GuestLinkCapped);
        using var details = System.Text.Json.JsonDocument.Parse(audit.Details);
        details.RootElement.GetProperty("grant").GetString().Should().Be(long48.ToString());
        details.RootElement.GetProperty("expires_at").GetDateTime().ToUniversalTime().Should().Be(end);
        details.RootElement.GetProperty("kind").GetString().Should().Be("tenant");
    }

    [Fact]
    public async Task SettingAnEndDateCapsAGuestLinkAndAnAlertChannelTheMemberMade()
    {
        var seeded = await SeedAsync("cap-both", accessEndsAt: null);
        var owner = await NewSubjectAsync(seeded, "cap-both-owner");
        var link = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(48));
        var rule = await SaveRuleWithChannelAsync(seeded, addedBy: seeded.Guest);
        var end = Now.UtcDateTime.AddDays(1);

        (await SetEndAsync(seeded, Sessions(), end)).Should().Be(OwnerGuardedChange.Changed);

        (await LinkAsync(seeded, link)).ExpiresAt.Should().Be(end);
        (await ChannelStopAsync(seeded, rule)).Should().Be(end);
        (await MembershipRowsAsync(seeded)).Single().AccessEndsAt.Should().Be(end);
    }

    private async Task<Guid> SaveRuleWithChannelAsync(Seeded seeded, Guid addedBy)
    {
        List<AlertRuleChannelWrite> channels;
        await using (var db = Context(seeded.Tenant))
        {
            channels = await new AlertRuleChannelWriter(Mock.Of<ISecretEncryptionService>()).ToWritesAsync(
                [new CreateAlertRuleChannelRequest { ChannelType = ChannelType.DiscordChannel, Destination = "123456789012345678" }],
                addedBy, null, db, CancellationToken.None);
        }

        return await new AlertRuleRepository(seeded.Contexts, new FakeTimeProvider(Now)).CreateAsync(
            new NewAlertRule(
                new AlertRuleWrite(
                    "Urgent low", null, AlertConditionType.Threshold, """{"direction":"below","value":55}""",
                    RuleScopeClass.Low, AlertRuleSeverity.Critical, IsEnabled: true, SortOrder: 0,
                    AllowThroughDnd: true, AutoResolveEnabled: false, AutoResolveParams: null, ClientConfiguration: "{}"),
                channels),
            CancellationToken.None);
    }

    private async Task<DateTime?> ChannelStopAsync(Seeded seeded, Guid ruleId) =>
        (await new AlertRuleRepository(seeded.Contexts, new FakeTimeProvider(Now)).GetChannelsAsync([ruleId], CancellationToken.None))[ruleId]
        .Single().StopsAt;

    [Fact]
    public async Task ShorteningTheEndDateCapsTheLinksAgain()
    {
        var seeded = await SeedAsync("shorten", accessEndsAt: null);
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "shorten-owner");
        var first = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(40));
        var second = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(30));

        await SetEndAsync(seeded, sessions, Now.UtcDateTime.AddHours(36));
        (await LinkAsync(seeded, first)).ExpiresAt.Should().Be(Now.UtcDateTime.AddHours(36));
        (await LinkAsync(seeded, second)).ExpiresAt.Should().Be(Now.UtcDateTime.AddHours(30));

        await SetEndAsync(seeded, sessions, Now.UtcDateTime.AddHours(12));

        (await LinkAsync(seeded, first)).ExpiresAt.Should().Be(Now.UtcDateTime.AddHours(12));
        (await LinkAsync(seeded, second)).ExpiresAt.Should().Be(Now.UtcDateTime.AddHours(12));
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().HaveCount(3, "one row per link capped, in each change");
    }

    [Fact]
    public async Task MovingTheEndDateLaterOrClearingItChangesNoLink()
    {
        var seeded = await SeedAsync("lengthen", Now.UtcDateTime.AddDays(1));
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "lengthen-owner");
        var expiresAt = Now.UtcDateTime.AddHours(12);
        var link = await GuestLinkAsync(seeded, seeded.Guest, owner, expiresAt, sessions);

        (await SetEndAsync(seeded, sessions, Now.UtcDateTime.AddDays(3))).Should().Be(OwnerGuardedChange.Changed);
        (await MembershipRowsAsync(seeded)).Single().AccessEndsAt.Should().Be(Now.UtcDateTime.AddDays(3));
        (await LinkAsync(seeded, link)).ExpiresAt.Should().Be(expiresAt, "a later end date is not a cap");

        (await SetEndAsync(seeded, sessions, null)).Should().Be(OwnerGuardedChange.Changed);
        (await MembershipRowsAsync(seeded)).Single().AccessEndsAt.Should().BeNull();
        (await LinkAsync(seeded, link)).ExpiresAt.Should().Be(expiresAt, "clearing the end date lengthens nothing");
        sessions.TryGet(seeded.Tenant, link, out _).Should().BeTrue();
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().BeEmpty();
    }

    [Fact]
    public async Task ACapLeavesTheLinksAnotherMemberMadeAndTheOnesThisMemberMadeInAnotherTenant()
    {
        var seeded = await SeedAsync("cap-scope", accessEndsAt: null);
        var elsewhere = await SeedAsync("cap-scope-other", accessEndsAt: null);
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "cap-scope-owner");
        var otherMember = await NewSubjectAsync(seeded, "cap-scope-member");
        var expiresAt = Now.UtcDateTime.AddHours(48);
        var theirs = await GuestLinkAsync(seeded, otherMember, owner, expiresAt);
        var otherTenant = await GuestLinkAsync(elsewhere, seeded.Guest, elsewhere.Guest, expiresAt);

        await SetEndAsync(seeded, sessions, Now.UtcDateTime.AddHours(1));

        (await LinkAsync(seeded, theirs)).ExpiresAt.Should().Be(expiresAt);
        (await LinkAsync(elsewhere, otherTenant)).ExpiresAt.Should().Be(expiresAt);
        (await LinkAuditAsync(seeded, otherMember)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedEndDateCapsNothing()
    {
        var seeded = await SeedAsync("cap-refused", accessEndsAt: null);
        var owner = await NewSubjectAsync(seeded, "cap-refused-owner");
        var members = new TenantMembershipRepository(seeded.Contexts, AccountLinkRoutes.Off);
        var ownerRole = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(seeded.Tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();
        var ownerMember = (await members.FindMemberAsync(seeded.Tenant, owner, CancellationToken.None))!;
        var expiresAt = Now.UtcDateTime.AddHours(48);
        var link = await GuestLinkAsync(seeded, owner, owner, expiresAt);

        (await Service(seeded.Contexts, Sessions()).SetMemberAccessEndAsync(
                seeded.Tenant, ownerMember.Id, Now.UtcDateTime.AddHours(1), Now.UtcDateTime))
            .Should().Be(OwnerGuardedChange.OwnerAccessEnds);

        (await LinkAsync(seeded, link)).ExpiresAt.Should().Be(expiresAt);
        (await LinkAuditAsync(seeded, owner)).Should().BeEmpty();
    }

    [Fact]
    public async Task ACapThatFailsToCommitLeavesTheDateAndTheLinksAsTheyWere()
    {
        var seeded = await SeedAsync("cap-rollback", accessEndsAt: null);
        var owner = await NewSubjectAsync(seeded, "cap-rollback-owner");
        var expiresAt = Now.UtcDateTime.AddHours(48);
        var link = await GuestLinkAsync(seeded, seeded.Guest, owner, expiresAt);
        var memberId = (await MembershipRowsAsync(seeded)).Single().Id;

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(seeded.Contexts, seeded.Tenant))
        {
            var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
            await FluentActions.Awaiting(() => unit.CommitAsync(async ct =>
            {
                await members.SetAccessEndUnlessOwnerAsync(seeded.Tenant, memberId, Now.UtcDateTime.AddHours(1), Now.UtcDateTime, ct);
                await new OAuthGrantRepository(unit).CapGrantsCreatedByAsync(seeded.Guest, Now.UtcDateTime.AddHours(1), Now.UtcDateTime, ct);
                await unit.Context.SaveChangesAsync(ct);
                throw new InvalidOperationException("rolled back");
            })).Should().ThrowAsync<InvalidOperationException>();
        }

        (await LinkAsync(seeded, link)).ExpiresAt.Should().Be(expiresAt);
        (await MembershipRowsAsync(seeded)).Should().ContainSingle().Which.AccessEndsAt.Should().BeNull();
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().BeEmpty();
    }

    private sealed class RefuseGrantSaves : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<OAuthGrantEntity>().Any(e => e.State == EntityState.Modified))
                throw new InvalidOperationException("grant save refused");

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private NocturneDbContext ContextRefusingGrantSaves(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant, [new RefuseGrantSaves()]);

    [Fact]
    public async Task ASetMemberAccessEndThatFailsAfterTheCapIsStagedChangesNothingAndEvictsNothing()
    {
        var seeded = await SeedAsync("cap-service-rollback", accessEndsAt: null);
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "cap-service-rollback-owner");
        var expiresAt = Now.UtcDateTime.AddHours(48);
        var link = await GuestLinkAsync(seeded, seeded.Guest, owner, expiresAt, sessions);
        var memberId = (await MembershipRowsAsync(seeded)).Single().Id;
        var failing = new SuppliedContextFactory(() => ContextRefusingGrantSaves(seeded.Tenant));

        await FluentActions.Awaiting(() => Service(failing, sessions).SetMemberAccessEndAsync(
                seeded.Tenant, memberId, Now.UtcDateTime.AddHours(1), Now.UtcDateTime))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("grant save refused");

        (await MembershipRowsAsync(seeded)).Single().AccessEndsAt.Should().BeNull("the date and the cap commit together or not at all");
        (await LinkAsync(seeded, link)).ExpiresAt.Should().Be(expiresAt);
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().BeEmpty();
        await using (var db = Context(seeded.Tenant))
        {
            (await db.AuthAuditLog.AsNoTracking().AnyAsync(r =>
                    r.TargetSubjectId == seeded.Guest && r.EventType == AuthAuditEventType.AccessEndChanged))
                .Should().BeFalse();
        }

        sessions.TryGet(seeded.Tenant, link, out _).Should().BeTrue("a change that did not commit evicts nothing");
    }

    private MemberInviteService InviteService(Seeded seeded, GuestSessionCacheService sessions, bool staleInvites) =>
        new(
            new TenantMembershipRepository(seeded.Contexts, AccountLinkRoutes.Off),
            staleInvites ? new StaleInviteReads(seeded.Contexts) : new MemberInviteRepository(seeded.Contexts),
            new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off),
            seeded.Contexts,
            Mock.Of<IJwtService>(),
            Mock.Of<ITenantRoleService>(),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example" }),
            new FakeTimeProvider(Now),
            NullLogger<MemberInviteService>.Instance,
            AccountLinkRoutes.Off,
            new GrantRevocationService(sessions), InertTeamStaff.Announcer());

    private async Task<(string Token, Guid Link)> InviteAndLinkOfEndedMemberAsync(
        Seeded seeded, Guid dataOwner, GuestSessionCacheService sessions, bool claimLastUse)
    {
        var link = await GuestLinkAsync(seeded, seeded.Guest, dataOwner, Now.UtcDateTime.AddHours(48), sessions);
        var token = $"token-{Guid.NewGuid():N}";
        var invites = new MemberInviteRepository(seeded.Contexts);
        var inviteId = await invites.CreateAsync(
            new NewMemberInvite(
                seeded.Tenant, await NewOwnerAsync(seeded, $"owner-{Guid.NewGuid():N}"), HashUtils.Sha256Hex(token),
                [seeded.CaretakerRole], null, null, false, Now.UtcDateTime.AddDays(7), 1, Now.UtcDateTime, null),
            CancellationToken.None);
        if (claimLastUse)
            (await invites.TryClaimUseAsync(inviteId, Now.UtcDateTime, CancellationToken.None)).Should().Be(MemberInviteClaim.Claimed);
        return (token, link);
    }

    [Fact]
    public async Task JoiningAgainRevokesTheLiveLinksTheEndedMemberMadeAndEvictsTheirSessionsAfterTheCommit()
    {
        var seeded = await SeedAsync("rejoin-links", Now.UtcDateTime.AddDays(-1));
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "rejoin-links-owner");
        var (token, link) = await InviteAndLinkOfEndedMemberAsync(seeded, owner, sessions, claimLastUse: false);

        var result = await InviteService(seeded, sessions, staleInvites: false).AcceptInviteAsync(token, seeded.Guest, seeded.Tenant);

        result.Success.Should().BeTrue();
        (await LinkAsync(seeded, link)).RevokedAt.Should().NotBeNull();
        sessions.TryGet(seeded.Tenant, link, out _).Should().BeFalse();
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().ContainSingle()
            .Which.EventType.Should().Be(AuthAuditEventType.GuestLinkRevoked);
    }

    [Fact]
    public async Task AJoinRefusedAfterTheReplaceRollsBackTheLinkRevocationItsAuditRowAndTheEviction()
    {
        var seeded = await SeedAsync("rejoin-links-refused", Now.UtcDateTime.AddDays(-1));
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "rejoin-links-refused-owner");
        var (token, link) = await InviteAndLinkOfEndedMemberAsync(seeded, owner, sessions, claimLastUse: true);

        var result = await InviteService(seeded, sessions, staleInvites: true).AcceptInviteAsync(token, seeded.Guest, seeded.Tenant);

        result.ErrorCode.Should().Be("exhausted");
        (await LinkAsync(seeded, link)).RevokedAt.Should().BeNull();
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().BeEmpty();
        sessions.TryGet(seeded.Tenant, link, out _).Should().BeTrue("a refused join evicts nothing");
        (await MembershipRowsAsync(seeded)).Should().ContainSingle().Which.AccessEndsAt.Should().Be(Now.UtcDateTime.AddDays(-1));
    }

    [Fact]
    public async Task RemovingAMemberRevokesTheLinksTheyMadeAndLeavesTheOthers()
    {
        var seeded = await SeedAsync("remove-links", accessEndsAt: null);
        var elsewhere = await SeedAsync("remove-links-other", accessEndsAt: null);
        var sessions = Sessions();
        var owner = await NewSubjectAsync(seeded, "remove-links-owner");
        var otherMember = await NewSubjectAsync(seeded, "remove-links-member");
        var expiresAt = Now.UtcDateTime.AddHours(48);
        var made = await GuestLinkAsync(seeded, seeded.Guest, owner, expiresAt, sessions);
        var madeOnTheirOwnData = await GuestLinkAsync(seeded, seeded.Guest, seeded.Guest, expiresAt, sessions);
        var expired = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(-1));
        var anothers = await GuestLinkAsync(seeded, otherMember, owner, expiresAt, sessions);
        var otherTenant = await GuestLinkAsync(elsewhere, seeded.Guest, elsewhere.Guest, expiresAt);

        (await Service(seeded.Contexts, sessions).RemoveMemberAsync(seeded.Tenant, seeded.Guest)).Ok.Should().BeTrue();

        (await LinkAsync(seeded, made)).RevokedAt.Should().NotBeNull();
        (await LinkAsync(seeded, madeOnTheirOwnData)).RevokedAt.Should().NotBeNull();
        (await LinkAsync(seeded, expired)).RevokedAt.Should().BeNull("an expired link has nothing left to revoke");
        (await LinkAsync(seeded, anothers)).RevokedAt.Should().BeNull();
        (await LinkAsync(elsewhere, otherTenant)).RevokedAt.Should().BeNull();
        sessions.TryGet(seeded.Tenant, made, out _).Should().BeFalse();
        sessions.TryGet(seeded.Tenant, madeOnTheirOwnData, out _).Should().BeFalse();
        sessions.TryGet(seeded.Tenant, anothers, out _).Should().BeTrue();

        var audit = await LinkAuditAsync(seeded, seeded.Guest);
        audit.Should().HaveCount(2).And.OnlyContain(r => r.EventType == AuthAuditEventType.GuestLinkRevoked);
        audit.Select(r => System.Text.Json.JsonDocument.Parse(r.Details).RootElement.GetProperty("grant").GetString())
            .Should().BeEquivalentTo([made.ToString(), madeOnTheirOwnData.ToString()]);
        (await LinkAuditAsync(seeded, otherMember)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARemovalThatRollsBackRevokesNoLink()
    {
        var seeded = await SeedAsync("remove-rollback", accessEndsAt: null);
        var owner = await NewSubjectAsync(seeded, "remove-rollback-owner");
        var link = await GuestLinkAsync(seeded, seeded.Guest, owner, Now.UtcDateTime.AddHours(48));

        await using (var unit = await DbUnitOfWork.OpenForTenantAsync(seeded.Contexts, seeded.Tenant))
        {
            var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
            var member = (await members.FindMemberAsync(seeded.Tenant, seeded.Guest, CancellationToken.None))!;
            await FluentActions.Awaiting(() => unit.CommitAsync(async ct =>
            {
                await MembershipRemoval.StageAsync(unit, members, Revocation(), seeded.Tenant, member, MembershipEndHow.Removed, Now.UtcDateTime, ct);
                throw new InvalidOperationException("rolled back");
            })).Should().ThrowAsync<InvalidOperationException>();
        }

        (await LinkAsync(seeded, link)).RevokedAt.Should().BeNull();
        (await MembershipRowsAsync(seeded)).Should().ContainSingle();
        (await LinkAuditAsync(seeded, seeded.Guest)).Should().BeEmpty();
    }
}
