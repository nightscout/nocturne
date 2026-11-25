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
/// A member invite lives only while its maker is a member in force, on PostgreSQL under the runtime
/// role: removing the maker revokes the invites they made and audits each, an accept is refused when
/// its maker has been removed or their access has ended, and an invite made by a member in force or
/// by an owner is accepted as before.
/// </summary>
[Trait("Category", "Integration")]
public class MemberInviteMakerPostgresTests(MemberInviteMakerPostgresTests.Database database)
    : IClassFixture<MemberInviteMakerPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("member_invite_maker");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Seeded(Guid Tenant, Guid Owner, Guid Maker, Guid Joiner, Guid CaretakerRole);

    private sealed record AuditRow(string EventType, Guid? Target, string? Details)
    {
        public string? Invite
        {
            get
            {
                if (Details is null)
                    return null;
                using var document = JsonDocument.Parse(Details);
                return document.RootElement.TryGetProperty("invite", out var value) ? value.GetString() : null;
            }
        }
    }

    private NocturneDbContext Context(Guid tenant, Guid? actor)
    {
        var context = TestContexts.Context(database.AppConnectionString)
            .ActingAs(actor is { } subject ? new TestAuditActor { SubjectId = subject } : null);
        context.TenantId = tenant;
        return context;
    }

    private SuppliedContextFactory Contexts(Guid tenant, Guid? actor = null) => new(() => Context(tenant, actor));

    private async Task<Seeded> SeedAsync(string slug, DateTime? makerAccessEndsAt = null, bool makerJoins = true)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'invite-maker-test', true, now(), now())
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
        var ownerSubject = await subjects.CreateAsync(new NewSubject($"{slug}-owner"), Ct);
        var maker = await subjects.CreateAsync(new NewSubject($"{slug}-maker"), Ct);
        var joiner = await subjects.CreateAsync(new NewSubject($"{slug}-joiner"), Ct);
        (await members.AddMemberAsync(new NewTenantMember(tenant, ownerSubject, [owner]) { How = MembershipJoinHow.Direct, By = null }, Ct)).Should().BeTrue();
        if (makerJoins)
        {
            (await members.AddMemberAsync(
                new NewTenantMember(tenant, maker, [caretaker], AccessEndsAt: makerAccessEndsAt) { How = MembershipJoinHow.Direct, By = null }, Ct))
                .Should().BeTrue();
        }

        return new Seeded(tenant, ownerSubject, maker, joiner, caretaker);
    }

    private static GrantRevocationService Revocation() =>
        new(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions())));

    private static MemberInviteService InviteService(SuppliedContextFactory contexts, ITenantMembershipRepository? members = null) => new(
        members ?? new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
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

    private async Task<(Guid Id, string Token)> InviteByAsync(
        Seeded s, Guid maker, DateTime? expiresAt = null, int? maxUses = null)
    {
        var token = $"token-{Guid.NewGuid():N}";
        var id = await new MemberInviteRepository(Contexts(s.Tenant)).CreateAsync(
            new NewMemberInvite(
                s.Tenant, maker, HashUtils.Sha256Hex(token), [s.CaretakerRole], null, null, false,
                expiresAt ?? Now.UtcDateTime.AddDays(7), maxUses, Now.UtcDateTime, null),
            Ct);
        return (id, token);
    }

    private async Task<MemberInviteRecord> StoredAsync(Seeded s, Guid inviteId) =>
        (await new MemberInviteRepository(Contexts(s.Tenant)).GetForTenantAsync(s.Tenant, Ct)).Single(i => i.Id == inviteId);

    private async Task<List<AuditRow>> RevokedAuditAsync(Seeded s)
    {
        await using var db = Context(Guid.Empty, null);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(a => a.TenantId == s.Tenant && a.EventType == AuthAuditEventType.MemberInviteRevoked)
                .OrderBy(a => a.Id)
                .ToListAsync())
            .Select(a => new AuditRow(a.EventType, a.TargetSubjectId, a.DetailsJson))
            .ToList();
    }

    private async Task<bool> IsMemberAsync(Seeded s, Guid subject)
    {
        await using var db = Context(Guid.Empty, null);
        return await db.TenantMembers.AnyAsync(m => m.TenantId == s.Tenant && m.SubjectId == subject);
    }

    [Fact]
    public async Task RemovingAMemberRevokesTheLiveInvitesTheyMadeAndAuditsEachOne()
    {
        var s = await SeedAsync("removal");
        var (first, _) = await InviteByAsync(s, s.Maker);
        var (second, _) = await InviteByAsync(s, s.Maker, maxUses: 5);
        var (earlier, _) = await InviteByAsync(s, s.Maker);
        var (expired, _) = await InviteByAsync(s, s.Maker, expiresAt: Now.UtcDateTime.AddDays(-1));
        var (owners, _) = await InviteByAsync(s, s.Owner);
        var revokedBefore = Now.UtcDateTime.AddDays(-2);
        await new MemberInviteRepository(Contexts(s.Tenant)).RevokeAsync(earlier, s.Tenant, revokedBefore, Ct);

        (await TenantService(Contexts(s.Tenant, s.Owner)).RemoveMemberAsync(s.Tenant, s.Maker)).Ok.Should().BeTrue();

        (await StoredAsync(s, first)).RevokedAt.Should().Be(Now.UtcDateTime);
        (await StoredAsync(s, second)).RevokedAt.Should().Be(Now.UtcDateTime);
        (await StoredAsync(s, earlier)).RevokedAt.Should().Be(revokedBefore, "an invite already revoked keeps its first revocation");
        (await StoredAsync(s, expired)).RevokedAt.Should().BeNull("an invite that cannot be accepted anyway is not rewritten");
        (await StoredAsync(s, owners)).RevokedAt.Should().BeNull("an owner who is still an owner keeps their invites");

        var rows = await RevokedAuditAsync(s);
        rows.Select(r => r.Invite).Should().BeEquivalentTo([first.ToString(), second.ToString()]);
        rows.Should().OnlyContain(r => r.Target == s.Maker);
    }

    [Fact]
    public async Task AnInviteMadeByARemovedMemberIsRefusedForAnyone()
    {
        var s = await SeedAsync("removed-refused");
        var (_, token) = await InviteByAsync(s, s.Maker);
        await TenantService(Contexts(s.Tenant, s.Owner)).RemoveMemberAsync(s.Tenant, s.Maker);

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Joiner, s.Tenant)).ErrorCode.Should().Be("revoked");
        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Maker, s.Tenant)).ErrorCode.Should().Be("revoked");

        (await IsMemberAsync(s, s.Joiner)).Should().BeFalse();
        (await IsMemberAsync(s, s.Maker)).Should().BeFalse("a removed member cannot join again through a link they made");
    }

    [Fact]
    public async Task AnInviteWhoseMakerLeftWithoutTheRevocationIsStillRefused()
    {
        var s = await SeedAsync("left-quietly");
        var (id, token) = await InviteByAsync(s, s.Maker);
        var members = new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off);
        var maker = (await members.FindMemberAsync(s.Tenant, s.Maker, Ct))!;
        (await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(
                Contexts(s.Tenant), AccountLinkRoutes.Off, s.Tenant, maker.Id, MembershipEndHow.Removed, Now.UtcDateTime, Ct))
            .Outcome.Should().Be(OwnerGuardedChange.Changed);
        (await StoredAsync(s, id)).RevokedAt.Should().BeNull();

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Joiner, s.Tenant)).ErrorCode.Should().Be("revoked");

        (await IsMemberAsync(s, s.Joiner)).Should().BeFalse();
    }

    [Fact]
    public async Task AMakerRemovedAfterTheAcceptReadTheirMembershipStillRefusesTheClaim()
    {
        var s = await SeedAsync("claim-race");
        var (id, token) = await InviteByAsync(s, s.Maker, maxUses: 1);
        var members = new TenantMembershipRepository(Contexts(s.Tenant), AccountLinkRoutes.Off);
        var maker = (await members.FindMemberAsync(s.Tenant, s.Maker, Ct))!;
        (await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(
                Contexts(s.Tenant), AccountLinkRoutes.Off, s.Tenant, maker.Id, MembershipEndHow.Removed, Now.UtcDateTime, Ct))
            .Outcome.Should().Be(OwnerGuardedChange.Changed);

        var service = InviteService(Contexts(s.Tenant), new MakerStillReadsAsMember(Contexts(s.Tenant), s.Maker));

        (await service.AcceptInviteAsync(token, s.Joiner, s.Tenant)).ErrorCode.Should().Be("revoked");

        (await IsMemberAsync(s, s.Joiner)).Should().BeFalse();
        (await StoredAsync(s, id)).UseCount.Should().Be(0, "the refused claim gives no use");
    }

    [Fact]
    public async Task AnInviteMadeByAMemberWhoseAccessHasEndedIsRefusedAndListedAsNoLongerValid()
    {
        var s = await SeedAsync("ended", makerAccessEndsAt: Now.UtcDateTime.AddDays(-1));
        var (id, token) = await InviteByAsync(s, s.Maker);
        var service = InviteService(Contexts(s.Tenant));

        (await service.AcceptInviteAsync(token, s.Joiner, s.Tenant)).ErrorCode.Should().Be("revoked");
        (await service.AcceptInviteAsync(token, s.Maker, s.Tenant)).ErrorCode.Should().Be("revoked");

        (await IsMemberAsync(s, s.Joiner)).Should().BeFalse();
        (await service.GetInviteByTokenAsync(token, s.Tenant))!.IsValid.Should().BeFalse();
        (await service.GetInvitesForTenantAsync(s.Tenant)).Single(i => i.Id == id).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task AnInviteStopsWorkingAtTheInstantItsMakersAccessEnds()
    {
        var s = await SeedAsync("ends-at", makerAccessEndsAt: Now.UtcDateTime);
        var (_, token) = await InviteByAsync(s, s.Maker);

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Joiner, s.Tenant)).ErrorCode.Should().Be("revoked");
    }

    [Fact]
    public async Task AnInviteMadeByAMemberWhoseAccessStillRunsIsAccepted()
    {
        var s = await SeedAsync("live-maker", makerAccessEndsAt: Now.UtcDateTime.AddDays(3));
        var (_, token) = await InviteByAsync(s, s.Maker);

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Joiner, s.Tenant)).Success.Should().BeTrue();

        (await IsMemberAsync(s, s.Joiner)).Should().BeTrue();
    }

    [Fact]
    public async Task AnInviteMadeByAnOwnerIsAccepted()
    {
        var s = await SeedAsync("owner-maker");
        var (_, token) = await InviteByAsync(s, s.Owner);

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Joiner, s.Tenant)).Success.Should().BeTrue();

        (await IsMemberAsync(s, s.Joiner)).Should().BeTrue();
    }

    [Fact]
    public async Task AnInviteSurvivesTheRemovalOfSomeoneWhoDidNotMakeIt()
    {
        var s = await SeedAsync("bystander");
        var (_, token) = await InviteByAsync(s, s.Owner);
        await TenantService(Contexts(s.Tenant)).AddMemberAsync(s.Tenant, s.Joiner, [s.CaretakerRole]);
        var bystander = await new SubjectRepository(Contexts(s.Tenant), AccountLinkRoutes.Off).CreateAsync(new NewSubject("bystander-other"), Ct);

        (await TenantService(Contexts(s.Tenant, s.Owner)).RemoveMemberAsync(s.Tenant, s.Joiner)).Ok.Should().BeTrue();

        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, bystander, s.Tenant)).Success.Should().BeTrue();
        (await RevokedAuditAsync(s)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARemovalRefusedForTheLastOwnerLeavesTheirInvitesLive()
    {
        var s = await SeedAsync("last-owner", makerJoins: false);
        var (id, token) = await InviteByAsync(s, s.Owner);

        (await TenantService(Contexts(s.Tenant, s.Owner)).RemoveMemberAsync(s.Tenant, s.Owner)).Ok.Should().BeFalse();

        (await StoredAsync(s, id)).RevokedAt.Should().BeNull();
        (await RevokedAuditAsync(s)).Should().BeEmpty();
        (await InviteService(Contexts(s.Tenant)).AcceptInviteAsync(token, s.Joiner, s.Tenant)).Success.Should().BeTrue();
    }

    [Fact]
    public async Task ThePendingListNamesWhoMadeEachInvite()
    {
        var s = await SeedAsync("list");
        var (byMaker, _) = await InviteByAsync(s, s.Maker);
        var (byOwner, _) = await InviteByAsync(s, s.Owner);

        var list = await InviteService(Contexts(s.Tenant)).GetInvitesForTenantAsync(s.Tenant);

        list.Single(i => i.Id == byMaker).CreatedByName.Should().Be("list-maker");
        list.Single(i => i.Id == byOwner).CreatedByName.Should().Be("list-owner");
        list.Should().OnlyContain(i => i.IsValid);
    }

    [Fact]
    public async Task ThePendingListMarksOnlyTheInvitesOfMakersNoLongerInForceAsInvalid()
    {
        var s = await SeedAsync("list-mixed", makerAccessEndsAt: Now.UtcDateTime.AddDays(-1));
        var (byEnded, _) = await InviteByAsync(s, s.Maker);
        var (byOwner, _) = await InviteByAsync(s, s.Owner);

        var list = await InviteService(Contexts(s.Tenant)).GetInvitesForTenantAsync(s.Tenant);

        list.Single(i => i.Id == byEnded).Should().Match<MemberInviteInfo>(i => !i.IsValid && i.IsRevoked);
        list.Single(i => i.Id == byOwner).IsValid.Should().BeTrue();
    }

    /// <summary>Reads the maker as a member, as an accept that checked them before their removal committed does.</summary>
    private sealed class MakerStillReadsAsMember(INocturneDbContextFactory contexts, Guid maker)
        : TenantMembershipRepository(contexts, AccountLinkRoutes.Off)
    {
        public override Task<bool> IsMemberAsync(Guid tenantId, Guid subjectId, DateTime now, CancellationToken ct) =>
            subjectId == maker ? Task.FromResult(true) : base.IsMemberAsync(tenantId, subjectId, now, ct);
    }
}
