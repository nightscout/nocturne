using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.API.Multitenancy;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Chat;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
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

    private sealed class Factory(Func<Guid, NocturneDbContext> create, Guid scope) : INocturneDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) => ValueTask.FromResult(create(scope));

        public ValueTask<NocturneDbContext> CreateForTenantAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));

        public ValueTask<NocturneDbContext> CreateForSubjectAsync(Guid subjectId, CancellationToken ct = default)
        {
            var context = create(Guid.Empty);
            context.SubjectId = subjectId;
            return ValueTask.FromResult(context);
        }

        public ValueTask<NocturneDbContext> CreatePlatformAsync(CancellationToken ct = default) => ValueTask.FromResult(create(Guid.Empty));

        public ValueTask<NocturneDbContext> CreateSystemPinnedAsync(Guid tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(create(tenantId));
    }

    private NocturneDbContext Context(Guid tenant) =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.AppConnectionString)
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options) { TenantId = tenant };

    private static GrantRevocationService Revocation() =>
        new(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())));

    private sealed record Seeded(Guid Tenant, Guid Guest, Guid CaretakerRole, Factory Contexts);

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

        var contexts = new Factory(Context, tenant);
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, CancellationToken.None);
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        var guest = await new SubjectRepository(contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-guest"), CancellationToken.None);
        (await members.AddMemberAsync(new NewTenantMember(tenant, guest, [caretaker], AccessEndsAt: accessEndsAt), CancellationToken.None))
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
                (await members.AddMemberAsync(new NewTenantMember(seeded.Tenant, seeded.Guest, [seeded.CaretakerRole]), ct)).Should().BeTrue();
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
        var platform = new Factory(Context, Guid.Empty);
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

        (await new TenantMembershipRepository(seeded.Contexts, AccountLinkRoutes.Off).SetAccessEndUnlessOwnerAsync(
            seeded.Tenant, (await MembershipRowsAsync(seeded)).Single().Id, Now.UtcDateTime.AddDays(7), clock.GetUtcNow().UtcDateTime,
            CancellationToken.None)).Should().Be(OwnerGuardedChange.Changed);
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
        var platform = new Factory(Context, Guid.Empty);
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
                tenant, state.Seeded.Guest, HashUtils.Sha256Hex(token), [state.Seeded.CaretakerRole], null, null, false,
                Now.UtcDateTime.AddDays(7), 1, Now.UtcDateTime, null),
            CancellationToken.None);
        (await invites.TryClaimUseAsync(inviteId, CancellationToken.None)).Should().BeTrue();
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
            Revocation());

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
            Revocation());

        var result = await service.ApproveRequestAsync(
            requestId, tenant, [state.Seeded.CaretakerRole], Guid.NewGuid(), [Scope.FullAccess], accessEndsAt: null);

        result.Error.Should().Be("Request is no longer pending.");
        await ShouldBeUntouchedAsync(state);
        (await new SubjectRepository(state.Seeded.Contexts, AccountLinkRoutes.Off).GetAsync(state.Seeded.Guest, CancellationToken.None))!
            .Should().Match<SubjectRecord>(s => !s.IsActive && s.ApprovalStatus == "Pending", "the activation rolled back with the rest");
    }
}
