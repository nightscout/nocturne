using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="MemberHistoryReader"/> on PostgreSQL under the runtime role: only the asked tenant's
/// member changes come back, in a stable newest-first order, with names and invite labels resolved.
/// </summary>
[Trait("Category", "Integration")]
public class MemberHistoryReaderPostgresTests(TenantMembershipRepositoryPostgresTests.Database database)
    : IClassFixture<TenantMembershipRepositoryPostgresTests.Database>
{
    private static readonly DateTime Start = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private sealed record Seeded(Guid Tenant, Guid Member, Guid Actor, MemberHistoryReader Reader);

    private async Task<Seeded> SeedAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'member-history-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var factory = new SuppliedContextFactory(() => Context(tenant));
        var subjects = new SubjectRepository(factory, AccountLinkRoutes.Off);
        var member = await subjects.CreateAsync(new NewSubject($"{slug}-member"), CancellationToken.None);
        var actor = await subjects.CreateAsync(new NewSubject($"{slug}-actor"), CancellationToken.None);
        return new Seeded(tenant, member, actor, new MemberHistoryReader(factory));
    }

    private async Task<Guid> AddAsync(
        Guid? tenant, string eventType, string? details, Guid? target = null, Guid? actor = null,
        string? credential = null, int minute = 0, Guid? id = null)
    {
        var row = new AuthAuditLogEntity
        {
            Id = id ?? Guid.CreateVersion7(),
            EventType = eventType,
            TenantId = tenant,
            TargetSubjectId = target,
            ActorSubjectId = actor,
            ActorCredential = credential,
            DetailsJson = details,
            CreatedAt = Start.AddMinutes(minute),
        };
        await using var db = Context(Guid.Empty);
        db.AuthAuditLog.Add(row);
        await db.SaveChangesAsync();
        await db.AuthAuditLog.Where(a => a.Id == row.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.CreatedAt, Start.AddMinutes(minute)));
        return row.Id;
    }

    private static string Tenant(string key, string value) => $$"""{"{{key}}":"{{value}}","kind":"tenant"}""";

    private static Task<MemberHistoryPage> Read(
        Seeded s, Guid? member = null, int limit = 25, int offset = 0) =>
        s.Reader.GetAsync(s.Tenant, member, limit, offset, DateTime.UtcNow, CancellationToken.None);

    [Fact]
    public async Task ARowOfAnotherTenantIsNeverReturned()
    {
        var a = await SeedAsync("ha");
        var b = await SeedAsync("hb");
        var mine = await AddAsync(a.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), a.Member, a.Actor);
        await AddAsync(b.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), b.Member, b.Actor);
        await AddAsync(null, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), a.Member, a.Actor);

        var page = await Read(a);

        page.Data.Select(e => e.Id).Should().Equal(mine);
        page.Pagination.Total.Should().Be(1);
    }

    [Fact]
    public async Task OnlyTheMemberChangeEventTypesOfTenantKindAreReturned()
    {
        var s = await SeedAsync("hb2");
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, """{"role":"owner","kind":"global"}""", s.Member, s.Actor);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, """{"role":"owner"}""", s.Member, s.Actor);
        await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, """{"access_ends_at":null,"kind":"global"}""", s.Member, s.Actor);
        await AddAsync(s.Tenant, AuthAuditEventType.RolePermissionsChanged,
            """{"role":"Admin","gained":["a"],"lost":[],"kind":"global"}""", actor: s.Actor);
        await AddAsync(s.Tenant, AuthAuditEventType.Login, Tenant("role", "viewer"), s.Member, s.Member);
        await AddAsync(s.Tenant, AuthAuditEventType.TokenIssued, null, s.Member, s.Member);
        await AddAsync(s.Tenant, AuthAuditEventType.SubjectUpdated, Tenant("role", "viewer"), s.Member, s.Actor);
        var role = await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), s.Member, s.Actor, minute: 1);
        var removed = await AddAsync(s.Tenant, AuthAuditEventType.RoleRemoved, Tenant("permission", "glucose.read"), s.Member, s.Actor, minute: 2);
        var limit = await AddAsync(s.Tenant, AuthAuditEventType.HistoryLimitChanged, Tenant("history_limit", "24_hours"), s.Member, s.Actor, minute: 3);
        var end = await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, Tenant("access_ends_at", "2026-04-02T13:00:00.0000000Z"), s.Member, s.Actor, minute: 4);
        var changed = await AddAsync(s.Tenant, AuthAuditEventType.RolePermissionsChanged,
            """{"role_id":"x","role":"Caretaker","gained":["b","a"],"lost":["c"],"kind":"tenant"}""", actor: s.Actor, minute: 5);

        var page = await Read(s);

        page.Data.Select(e => e.Id).Should().Equal(changed, end, limit, removed, role);
        page.Pagination.Total.Should().Be(5);
    }

    [Fact]
    public async Task EntriesAreNewestFirstAndAPageBoundaryKeepsRowsWithTheSameTimeInOrder()
    {
        var s = await SeedAsync("hc");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add(await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", $"r{i}"), s.Member, s.Actor,
                minute: i / 2, id: Guid.Parse($"00000000-0000-7000-8000-00000000000{i}")));

        var expected = new[] { ids[4], ids[3], ids[2], ids[1], ids[0] };
        var first = await Read(s, limit: 2);
        var second = await Read(s, limit: 2, offset: 2);
        var third = await Read(s, limit: 2, offset: 4);

        first.Data.Select(e => e.Id).Concat(second.Data.Select(e => e.Id)).Concat(third.Data.Select(e => e.Id))
            .Should().Equal(expected);
        first.Pagination.Total.Should().Be(5);
        first.Data.First().At.Should().Be(Start.AddMinutes(2));
    }

    [Fact]
    public async Task TheMemberFilterMatchesTheTargetAndDropsRoleChangesWithNoTarget()
    {
        var s = await SeedAsync("hd");
        var other = await new SubjectRepository(new SuppliedContextFactory(() => Context(s.Tenant)), AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject("hd-other"), CancellationToken.None);
        var mine = await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), s.Member, s.Actor);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), other, s.Actor);
        await AddAsync(s.Tenant, AuthAuditEventType.RolePermissionsChanged,
            """{"role":"Caretaker","gained":["a"],"lost":[],"kind":"tenant"}""", actor: s.Actor);

        var filtered = await Read(s, member: s.Member);

        filtered.Data.Select(e => e.Id).Should().Equal(mine);
        filtered.Pagination.Total.Should().Be(1);
        (await Read(s)).Pagination.Total.Should().Be(3);
    }

    [Fact]
    public async Task ARowWithClearedDetailsIsReturnedWithTheDetailUnknown()
    {
        var s = await SeedAsync("he");
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, null, s.Member, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleRemoved, null, s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.HistoryLimitChanged, null, s.Member, s.Actor, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, null, s.Member, s.Actor, minute: 4);
        await AddAsync(s.Tenant, AuthAuditEventType.RolePermissionsChanged, null, actor: s.Actor, minute: 5);

        var page = (await Read(s)).Data.ToList();

        page.Select(e => e.Kind).Should().Equal(
            MemberHistoryKind.RolePermissionsChanged, MemberHistoryKind.AccessEndChanged, MemberHistoryKind.HistoryLimitChanged,
            MemberHistoryKind.AccessTaken, MemberHistoryKind.AccessGiven);
        page.Should().OnlyContain(e => e.Role == null && e.Permission == null && e.HistoryLimit == null
            && e.Gained == null && e.Lost == null && e.AccessEndsAt == null && !e.ViaInvite && e.InviteLabel == null);
    }

    [Fact]
    public async Task EachKindReadsItsDetails()
    {
        var s = await SeedAsync("hf");
        await AddAsync(s.Tenant, AuthAuditEventType.RoleRemoved, Tenant("role", "viewer"), s.Member, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("permission", "glucose.read"), s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.HistoryLimitChanged, Tenant("history_limit", "full"), s.Member, s.Actor, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.RolePermissionsChanged,
            """{"role_id":"x","role":"Caretaker","gained":["b","a"],"lost":["c"],"kind":"tenant"}""", actor: s.Actor, minute: 4);

        var page = (await Read(s)).Data.ToList();

        page[0].Should().Match<MemberHistoryEntry>(e => e.Kind == MemberHistoryKind.RolePermissionsChanged
            && e.Role == "Caretaker" && e.Member == null);
        page[0].Gained.Should().Equal("b", "a");
        page[0].Lost.Should().Equal("c");
        page[1].HistoryLimit.Should().Be(MemberHistoryLimit.Full);
        page[2].Should().Match<MemberHistoryEntry>(e => e.Kind == MemberHistoryKind.PermissionGiven && e.Permission == "glucose.read");
        page[3].Should().Match<MemberHistoryEntry>(e => e.Kind == MemberHistoryKind.RoleTaken && e.Role == "viewer");
    }

    [Fact]
    public async Task AnAccessEndRowReadsAsSetClearedOrUnknown()
    {
        var s = await SeedAsync("hk");
        await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, Tenant("access_ends_at", "2026-04-02T13:00:00.0000000Z"), s.Member, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, """{"access_ends_at":null,"kind":"tenant"}""", s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, """{"kind":"tenant"}""", s.Member, s.Actor, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.AccessEndChanged, Tenant("access_ends_at", "not a date"), s.Member, s.Actor, minute: 4);

        var page = (await Read(s)).Data.ToList();

        page.Select(e => (e.Kind, e.AccessEndsAt)).Should().Equal(
            (MemberHistoryKind.AccessEndChanged, (DateTime?)null),
            (MemberHistoryKind.AccessEndChanged, (DateTime?)null),
            (MemberHistoryKind.AccessEndCleared, (DateTime?)null),
            (MemberHistoryKind.AccessEndSet, (DateTime?)new DateTime(2026, 4, 2, 13, 0, 0, DateTimeKind.Utc)));
        page[3].AccessEndsAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task TheRealWritersAccessEndRowsReadBackAsSetThenCleared()
    {
        var s = await SeedAsync("hl");
        var factory = new SuppliedContextFactory(() => Context(s.Tenant));
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(s.Tenant, CancellationToken.None);
        var caretaker = (await members.FindRoleIdBySlugAsync(s.Tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(new NewTenantMember(s.Tenant, s.Member, [caretaker]) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();
        var membership = (await members.FindMemberAsync(s.Tenant, s.Member, CancellationToken.None))!;
        var end = new DateTime(2031, 6, 30, 14, 0, 0, DateTimeKind.Utc);

        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(factory, AccountLinkRoutes.Off, s.Tenant, membership.Id, end, DateTime.UtcNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.Changed);
        (await OwnerGuardedWrites.SetAccessEndUnlessOwnerAsync(factory, AccountLinkRoutes.Off, s.Tenant, membership.Id, null, DateTime.UtcNow, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.Changed);

        var entries = (await Read(s)).Data
            .Where(e => e.Kind is MemberHistoryKind.AccessEndSet or MemberHistoryKind.AccessEndCleared)
            .ToList();

        entries.Select(e => (e.Kind, e.AccessEndsAt)).Should().Equal(
            (MemberHistoryKind.AccessEndCleared, (DateTime?)null),
            (MemberHistoryKind.AccessEndSet, (DateTime?)end));
        entries.Should().OnlyContain(e => e.Member == new MemberHistoryPerson(s.Member, "hl-member"));
    }

    [Fact]
    public async Task AnInviteLabelResolvesAndAMissingInviteDoesNotBreakTheRow()
    {
        var s = await SeedAsync("hg");
        var labelled = Guid.NewGuid();
        var unlabelled = Guid.NewGuid();
        await using (var db = Context(s.Tenant))
        {
            db.MemberInvites.AddRange(
                Invite(s, labelled, "Family invite", "a"),
                Invite(s, unlabelled, null, "b"));
            await db.SaveChangesAsync();
        }
        string Via(Guid invite) => $$"""{"role":"viewer","kind":"tenant","invite":"{{invite}}"}""";
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(labelled), s.Member, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(unlabelled), s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(Guid.NewGuid()), s.Member, s.Actor, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), s.Member, s.Actor, minute: 4);

        var page = (await Read(s)).Data.ToList();

        page.Select(e => (e.ViaInvite, e.InviteLabel)).Should().Equal(
            (false, null), (true, null), (true, null), (true, "Family invite"));
    }

    private static MemberInviteEntity Invite(Seeded s, Guid id, string? label, string hash) => new()
    {
        Id = id, TenantId = s.Tenant, CreatedBySubjectId = s.Actor, TokenHash = hash + id.ToString("N"),
        Label = label, ExpiresAt = Start.AddDays(7), CreatedAt = Start,
    };

    [Fact]
    public async Task NamesResolveAndADeletedSubjectGivesANullName()
    {
        var s = await SeedAsync("hh");
        var gone = await new SubjectRepository(new SuppliedContextFactory(() => Context(s.Tenant)), AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject("hh-gone"), CancellationToken.None);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), s.Member, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), gone, gone, minute: 2);
        await using (var db = Context(Guid.Empty))
        {
            await db.Subjects.Where(x => x.Id == gone).ExecuteDeleteAsync();
        }

        var page = (await Read(s)).Data.ToList();

        page[0].Member.Should().Be(new MemberHistoryPerson(gone, null));
        page[0].Actor.Should().Be(new MemberHistoryActor(MemberHistoryActorKind.Person, gone, null));
        page[1].Member!.Name.Should().Be("hh-member");
        page[1].Actor.Should().Be(new MemberHistoryActor(MemberHistoryActorKind.Person, s.Actor, "hh-actor"));
    }

    [Fact]
    public async Task TheRealWritersRowsReadBackWithEachKindMemberActorAndInvite()
    {
        var s = await SeedAsync("hj");
        var factory = new SuppliedContextFactory(() => Context(s.Tenant));
        var members = new TenantMembershipRepository(factory, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(s.Tenant, CancellationToken.None);
        var caretaker = (await members.FindRoleIdBySlugAsync(s.Tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        var invite = Guid.NewGuid();
        await using (var db = Context(s.Tenant))
        {
            db.MemberInvites.Add(Invite(s, invite, "Round trip invite", "rt"));
            await db.SaveChangesAsync();
        }

        (await members.AddMemberAsync(
            new NewTenantMember(s.Tenant, s.Member, [caretaker], [Scope.GlucoseRead], LimitTo24Hours: true,
                CreatedFromInviteId: invite, MadeBy: MembershipMadeBy.Member) { How = MembershipJoinHow.Direct, By = null },
            CancellationToken.None)).Should().BeTrue();
        var membership = (await members.FindMemberAsync(s.Tenant, s.Member, CancellationToken.None))!;
        (await members.SetDirectPermissionsAsync(s.Tenant, membership.Id, [Scope.TreatmentsRead], DateTime.UtcNow, CancellationToken.None))
            .Should().BeTrue();
        (await members.SetLimitTo24HoursAsync(s.Tenant, membership.Id, false, DateTime.UtcNow, CancellationToken.None))
            .Should().BeTrue();

        var entries = (await Read(s)).Data.ToList();

        var self = new MemberHistoryActor(MemberHistoryActorKind.Person, s.Member, "hj-member");
        var system = new MemberHistoryActor(MemberHistoryActorKind.System, null, null);
        var person = new MemberHistoryPerson(s.Member, "hj-member");
        entries.Should().HaveCount(7);
        entries.Should().OnlyContain(e => e.Member == person);

        var byInvite = entries.Where(e => e.ViaInvite).ToList();
        byInvite.Should().HaveCount(4).And.OnlyContain(e => e.Actor == self && e.InviteLabel == "Round trip invite");
        byInvite.Select(e => (e.Kind, e.Role, e.Permission, e.HistoryLimit)).Should().BeEquivalentTo(new[]
        {
            (MemberHistoryKind.Joined, (string?)null, (string?)null, (MemberHistoryLimit?)null),
            (MemberHistoryKind.RoleGiven, RoleSeeds.Caretaker, (string?)null, (MemberHistoryLimit?)null),
            (MemberHistoryKind.PermissionGiven, (string?)null, Scope.GlucoseRead, (MemberHistoryLimit?)null),
            (MemberHistoryKind.HistoryLimitChanged, (string?)null, (string?)null, (MemberHistoryLimit?)MemberHistoryLimit.TwentyFourHours),
        });

        var later = entries.Where(e => !e.ViaInvite).ToList();
        later.Should().HaveCount(3).And.OnlyContain(e => e.Actor == system && e.InviteLabel == null);
        later.Select(e => (e.Kind, e.Permission, e.HistoryLimit)).Should().BeEquivalentTo(new[]
        {
            (MemberHistoryKind.PermissionTaken, (string?)Scope.GlucoseRead, (MemberHistoryLimit?)null),
            (MemberHistoryKind.PermissionGiven, (string?)Scope.TreatmentsRead, (MemberHistoryLimit?)null),
            (MemberHistoryKind.HistoryLimitChanged, (string?)null, (MemberHistoryLimit?)MemberHistoryLimit.Full),
        });
    }

    [Theory]
    [InlineData("system", MemberHistoryActorKind.System)]
    [InlineData("anonymous", MemberHistoryActorKind.Anonymous)]
    [InlineData("None", MemberHistoryActorKind.Anonymous)]
    [InlineData("ApiKey:abc", MemberHistoryActorKind.ApiKey)]
    [InlineData("DirectGrant:7f", MemberHistoryActorKind.ApiKey)]
    [InlineData("OAuthAccessToken:7f", MemberHistoryActorKind.AccessToken)]
    [InlineData("OidcToken", MemberHistoryActorKind.AccessToken)]
    [InlineData("LegacyJwt", MemberHistoryActorKind.AccessToken)]
    [InlineData("SessionCookie", MemberHistoryActorKind.AccessToken)]
    [InlineData("InstanceKey:ff", MemberHistoryActorKind.InstanceKey)]
    [InlineData("Guest:7f", MemberHistoryActorKind.GuestLink)]
    [InlineData("PlatformAccess:7f", MemberHistoryActorKind.PlatformAccess)]
    [InlineData("unknown", MemberHistoryActorKind.Unknown)]
    [InlineData("Something:else", MemberHistoryActorKind.Unknown)]
    [InlineData(null, MemberHistoryActorKind.Unknown)]
    public async Task ANonPersonActorIsMappedFromTheRecordedCredential(string? credential, MemberHistoryActorKind expected)
    {
        var s = await SeedAsync("hi");
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Tenant("role", "viewer"), s.Member, credential: credential);

        var entry = (await Read(s)).Data.Single();

        entry.Actor.Should().Be(new MemberHistoryActor(expected, null, null));
    }

    private static string Joined(string how, Guid? invite = null, Guid? by = null)
    {
        var details = new Dictionary<string, string> { ["kind"] = "tenant", ["how"] = how };
        if (invite is { } i)
            details["invite"] = i.ToString();
        if (by is { } b)
            details["by"] = b.ToString();
        return System.Text.Json.JsonSerializer.Serialize(details);
    }

    [Fact]
    public async Task JoinedAndRemovedRowsReadWithHowTheResponsiblePersonAndTheInviteLabel()
    {
        var s = await SeedAsync("jr");
        var other = await SeedAsync("jr-other");
        var invite = Guid.NewGuid();
        await using (var db = Context(s.Tenant))
        {
            db.MemberInvites.Add(Invite(s, invite, "Clinic", "jr"));
            await db.SaveChangesAsync();
        }

        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("invite", invite, s.Actor), s.Member, s.Member, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("request", by: s.Actor), s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("direct"), s.Member, s.Actor, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("setup"), s.Member, s.Member, minute: 4);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("system"), s.Member, credential: "system", minute: 5);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, Joined("removed"), s.Member, s.Actor, minute: 6);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, Joined("left"), s.Member, s.Member, minute: 7);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, Joined("account_deleted"), s.Member, s.Actor, minute: 8);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, Joined("replaced"), s.Member, s.Member, minute: 9);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, Joined("system"), s.Member, credential: "system", minute: 10);
        await AddAsync(other.Tenant, AuthAuditEventType.MemberJoined, Joined("direct"), s.Member, s.Actor, minute: 11);

        var page = (await Read(s)).Data.Reverse().ToList();

        page.Select(e => (e.Kind, e.JoinedHow, e.RemovedHow)).Should().Equal(
            (MemberHistoryKind.Joined, MembershipJoinHow.Invite, (MembershipEndHow?)null),
            (MemberHistoryKind.Joined, MembershipJoinHow.Request, (MembershipEndHow?)null),
            (MemberHistoryKind.Joined, MembershipJoinHow.Direct, (MembershipEndHow?)null),
            (MemberHistoryKind.Joined, MembershipJoinHow.Setup, (MembershipEndHow?)null),
            (MemberHistoryKind.Joined, MembershipJoinHow.System, (MembershipEndHow?)null),
            (MemberHistoryKind.Removed, (MembershipJoinHow?)null, MembershipEndHow.Removed),
            (MemberHistoryKind.Removed, (MembershipJoinHow?)null, MembershipEndHow.Left),
            (MemberHistoryKind.Removed, (MembershipJoinHow?)null, MembershipEndHow.AccountDeleted),
            (MemberHistoryKind.Removed, (MembershipJoinHow?)null, MembershipEndHow.Replaced),
            (MemberHistoryKind.Removed, (MembershipJoinHow?)null, MembershipEndHow.System));
        page[0].Should().Match<MemberHistoryEntry>(e => e.ViaInvite && e.InviteLabel == "Clinic"
            && e.By == new MemberHistoryPerson(s.Actor, "jr-actor") && e.Member == new MemberHistoryPerson(s.Member, "jr-member")
            && e.Actor == new MemberHistoryActor(MemberHistoryActorKind.Person, s.Member, "jr-member"));
        page[1].By.Should().Be(new MemberHistoryPerson(s.Actor, "jr-actor"));
        page.Skip(2).Should().OnlyContain(e => e.By == null && !e.ViaInvite);
    }

    [Fact]
    public async Task AJoinedOrRemovedRowWithClearedDetailsReadsWithHowUnknown()
    {
        var s = await SeedAsync("jx");
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, null, s.Member, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, null, s.Member, s.Actor, minute: 2);

        var page = (await Read(s)).Data.ToList();

        page.Select(e => (e.Kind, e.JoinedHow, e.RemovedHow, e.By)).Should().Equal(
            (MemberHistoryKind.Removed, (MembershipJoinHow?)null, (MembershipEndHow?)null, (MemberHistoryPerson?)null),
            (MemberHistoryKind.Joined, (MembershipJoinHow?)null, (MembershipEndHow?)null, (MemberHistoryPerson?)null));
    }

    [Fact]
    public async Task AChangeAnInviteMadeIsInARecordedJoinOnlyBesideAJoinedRowOfTheSameCommitForTheSameMemberAndInvite()
    {
        var s = await SeedAsync("jk");
        var recorded = Guid.NewGuid();
        var older = Guid.NewGuid();
        string Via(Guid invite) => $$"""{"role":"viewer","kind":"tenant","invite":"{{invite}}"}""";
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("invite", older), s.Actor, s.Actor, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(older), s.Member, s.Member, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("invite", recorded), s.Member, s.Member, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(recorded), s.Member, s.Member, minute: 3);

        var page = (await Read(s, s.Member)).Data.ToList();

        page.Select(e => (e.Kind, e.InRecordedJoin)).Should().BeEquivalentTo(new[]
        {
            (MemberHistoryKind.RoleGiven, true),
            (MemberHistoryKind.Joined, false),
            (MemberHistoryKind.RoleGiven, false),
        });
        page.Single(e => e.Kind == MemberHistoryKind.RoleGiven && e.InRecordedJoin).At.Should().Be(Start.AddMinutes(3));
    }

    [Fact]
    public async Task AnOldJoinByAMultiUseInviteKeepsItsWordingWhenTheSamePersonRejoinsThroughThatInvite()
    {
        var s = await SeedAsync("jm");
        var invite = Guid.NewGuid();
        string Via(Guid id) => $$"""{"role":"viewer","kind":"tenant","invite":"{{id}}"}""";
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(invite), s.Member, s.Member, minute: 1);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleRemoved, Tenant("role", "viewer"), s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberRemoved, Joined("removed"), s.Member, s.Actor, minute: 2);
        await AddAsync(s.Tenant, AuthAuditEventType.MemberJoined, Joined("invite", invite), s.Member, s.Member, minute: 3);
        await AddAsync(s.Tenant, AuthAuditEventType.RoleAssigned, Via(invite), s.Member, s.Member, minute: 3);

        var given = (await Read(s, s.Member)).Data.Where(e => e.Kind == MemberHistoryKind.RoleGiven).ToList();

        given.Select(e => (e.At, e.InRecordedJoin)).Should().Equal(
            (Start.AddMinutes(3), true),
            (Start.AddMinutes(1), false));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public async Task ANarrowedPageSaysWhenTheMembersAccessEndedOnlyOnceTheDateHasPassed(int? daysFromNow, bool ended)
    {
        var s = await SeedAsync("je");
        var now = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        DateTime? end = daysFromNow is { } days ? now.AddDays(days) : null;
        (await new TenantMembershipRepository(new SuppliedContextFactory(() => Context(s.Tenant)), AccountLinkRoutes.Off)
            .AddMemberAsync(new NewTenantMember(s.Tenant, s.Member, [], AccessEndsAt: end) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None)).Should().BeTrue();

        var narrowed = await s.Reader.GetAsync(s.Tenant, s.Member, 25, 0, now, CancellationToken.None);
        var unnarrowed = await s.Reader.GetAsync(s.Tenant, null, 25, 0, now, CancellationToken.None);

        narrowed.AccessEndedAt.Should().Be(ended ? end : null);
        unnarrowed.AccessEndedAt.Should().BeNull();
    }
}
