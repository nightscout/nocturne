using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Nocturne.API.Tests.Services.Identity;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Deleting a subject on PostgreSQL under the runtime role and Row Level Security stops what they
/// made in every tenant they belong to before the row goes: the guest links and device credentials
/// they made, the member invites they made, and the alert channels they added.
/// </summary>
[Trait("Category", "Integration")]
public class SubjectDeletionPostgresTests(SubjectDeletionPostgresTests.Database database)
    : IClassFixture<SubjectDeletionPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("subject_deletion");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Site(Guid Tenant, Guid Owner, Guid CaretakerRole);

    private sealed record Made(Guid GuestLink, Guid DeviceCredential, Guid Invite, Guid Rule);

    private readonly GuestSessionCacheService _sessions = new(new MemoryCache(new MemoryCacheOptions()));

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private SuppliedContextFactory Contexts(Guid tenant) => new(() => Context(tenant));

    private static DateTime UtcNow => DateTime.UtcNow;

    /// <summary>A tenant with its seed roles and an owner, the data owner of the guest links made on it.</summary>
    private async Task<Site> SeedSiteAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'subject-deletion-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var members = new TenantMembershipRepository(Contexts(tenant), AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, Ct);
        var ownerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var caretakerRole = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        var owner = await new SubjectRepository(Contexts(tenant), AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-owner"), Ct);
        (await members.AddMemberAsync(new NewTenantMember(tenant, owner, [ownerRole]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        return new Site(tenant, owner, caretakerRole);
    }

    private async Task<Guid> SeedMakerAsync(string slug, params Site[] sites)
    {
        var maker = await new SubjectRepository(Contexts(Guid.Empty), AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-maker"), Ct);
        foreach (var site in sites)
        {
            (await new TenantMembershipRepository(Contexts(site.Tenant), AccountLinkRoutes.Off).AddMemberAsync(
                    new NewTenantMember(site.Tenant, maker, [site.CaretakerRole]) { How = MembershipJoinHow.Direct, By = site.Owner }, Ct))
                .Should().BeTrue();
        }

        return maker;
    }

    /// <summary>
    /// A live, activated guest link to <paramref name="site"/>'s owner's data, a device credential, an
    /// invite and an alert rule channel, all made by <paramref name="maker"/>, with the link's
    /// session cached as a request would leave it.
    /// </summary>
    private async Task<Made> MakeAsync(Site site, Guid maker)
    {
        var guestLink = Guid.CreateVersion7();
        var deviceCredential = Guid.CreateVersion7();
        await using (var db = Context(site.Tenant))
        {
            db.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = guestLink,
                TenantId = site.Tenant,
                SubjectId = site.Owner,
                CreatedBySubjectId = maker,
                GrantType = OAuthGrantTypes.Guest,
                Scopes = [Scope.GlucoseRead],
                TokenHash = guestLink.ToString("N"),
                ExpiresAt = UtcNow.AddDays(7),
                ActivatedAt = UtcNow.AddHours(-1),
            });
            db.OAuthGrants.Add(new OAuthGrantEntity
            {
                Id = deviceCredential,
                TenantId = site.Tenant,
                SubjectId = site.Owner,
                CreatedBySubjectId = maker,
                GrantType = OAuthGrantTypes.Direct,
                Scopes = [Scope.GlucoseRead],
                TokenHash = deviceCredential.ToString("N"),
                ExpiresAt = null,
            });
            await db.SaveChangesAsync();
        }

        (await GuestLinks(site).ValidateSessionAsync(guestLink, Ct)).Should().NotBeNull("the link is live before the delete");
        _sessions.Set(site.Tenant, guestLink, await GuestLinks(site).ValidateSessionAsync(guestLink, Ct));

        var invite = await new MemberInviteRepository(Contexts(site.Tenant)).CreateAsync(
            new NewMemberInvite(
                site.Tenant, maker, HashUtils.Sha256Hex($"token-{Guid.NewGuid():N}"), [site.CaretakerRole], null, null, false,
                UtcNow.AddDays(7), null, UtcNow, null),
            Ct);

        var rule = await new AlertRuleRepository(Contexts(site.Tenant), TimeProvider.System).CreateAsync(
            new NewAlertRule(
                new AlertRuleWrite(
                    "Urgent low", null, AlertConditionType.Threshold, """{"direction":"below","value":55}""",
                    RuleScopeClass.Low, AlertRuleSeverity.Critical, IsEnabled: true, SortOrder: 0,
                    AllowThroughDnd: true, AutoResolveEnabled: false, AutoResolveParams: null, ClientConfiguration: "{}"),
                [new AlertRuleChannelWrite(ChannelType.Webhook, "https://example.invalid/hook", maker, null, null)]),
            Ct);

        return new Made(guestLink, deviceCredential, invite, rule);
    }

    private GuestLinkService GuestLinks(Site site)
    {
        var contexts = Contexts(site.Tenant);
        return new GuestLinkService(
            contexts, new OAuthGrantRepository(contexts), _sessions, new GrantRevocationService(_sessions),
            TimeProvider.System, NullLogger<GuestLinkService>.Instance);
    }

    private SubjectDeletion Deletion()
    {
        var contexts = Contexts(Guid.Empty);
        return new SubjectDeletion(
            contexts,
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantMembershipRepository(contexts, AccountLinkRoutes.Off),
            new GrantRevocationService(_sessions),
            new AuthAuditService(
                contexts, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, new AuditContext(),
                NullLogger<AuthAuditService>.Instance),
            TimeProvider.System,
            InertTeamStaff.Announcer(),
            InertTeamStaff.Sites(),
            NullLogger<SubjectDeletion>.Instance);
    }

    private async Task<OAuthGrantEntity> GrantAsync(Site site, Guid id)
    {
        await using var db = Context(site.Tenant);
        return await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == id);
    }

    private async Task<List<AuthAuditLogEntity>> AuditAsync(Site site, string eventType, Guid target)
    {
        await using var db = Context(site.Tenant);
        return await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.TenantId == site.Tenant && a.EventType == eventType && a.TargetSubjectId == target)
            .ToListAsync();
    }

    [Fact]
    public async Task DeletingASubjectRevokesTheGuestLinksTheyMadeInEveryTenantAndRefusesASessionOnEither()
    {
        var first = await SeedSiteAsync("del-links-a");
        var second = await SeedSiteAsync("del-links-b");
        var maker = await SeedMakerAsync("del-links", first, second);
        var madeOnFirst = await MakeAsync(first, maker);
        var madeOnSecond = await MakeAsync(second, maker);

        (await Deletion().DeleteAsync(maker, evenLastOwner: false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        foreach (var (site, made) in new[] { (first, madeOnFirst), (second, madeOnSecond) })
        {
            var link = await GrantAsync(site, made.GuestLink);
            link.RevokedAt.Should().NotBeNull();
            link.CreatedBySubjectId.Should().BeNull("the delete clears the maker once the link is revoked");
            _sessions.TryGet(site.Tenant, made.GuestLink, out _).Should().BeFalse("the cached session is evicted");
            (await GuestLinks(site).ValidateSessionAsync(made.GuestLink, Ct)).Should().BeNull();
            (await AuditAsync(site, AuthAuditEventType.GuestLinkRevoked, maker)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task DeletingASubjectEndsTheDeviceCredentialsTheyMadeAndStopsTheAlertChannelsTheyAdded()
    {
        var site = await SeedSiteAsync("del-made");
        var maker = await SeedMakerAsync("del-made", site);
        var made = await MakeAsync(site, maker);
        var before = UtcNow;

        (await Deletion().DeleteAsync(maker, evenLastOwner: false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        (await GrantAsync(site, made.DeviceCredential)).ExpiresAt.Should().NotBeNull().And.BeOnOrBefore(UtcNow).And.BeOnOrAfter(before);
        (await AuditAsync(site, AuthAuditEventType.DeviceCredentialCapped, maker)).Should().ContainSingle();
        await using var db = Context(site.Tenant);
        var channel = await db.AlertRuleChannels.AsNoTracking().SingleAsync(c => c.AlertRuleId == made.Rule);
        channel.StopsAt.Should().NotBeNull().And.BeOnOrBefore(UtcNow).And.BeOnOrAfter(before);
    }

    [Fact]
    public async Task DeletingASubjectRevokesTheLiveInvitesTheyMadeInEveryTenant()
    {
        var first = await SeedSiteAsync("del-invites-a");
        var second = await SeedSiteAsync("del-invites-b");
        var maker = await SeedMakerAsync("del-invites", first, second);
        await MakeAsync(first, maker);
        await MakeAsync(second, maker);

        (await Deletion().DeleteAsync(maker, evenLastOwner: false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        foreach (var site in new[] { first, second })
        {
            (await AuditAsync(site, AuthAuditEventType.MemberInviteRevoked, maker)).Should().ContainSingle();
            await using var db = Context(site.Tenant);
            (await db.MemberInvites.AnyAsync(i => i.CreatedBySubjectId == maker)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task DeletingASubjectLeavesWhatAnotherMemberMadeLive()
    {
        var site = await SeedSiteAsync("del-other");
        var maker = await SeedMakerAsync("del-other-leaver", site);
        var stayer = await SeedMakerAsync("del-other-stayer", site);
        var kept = await MakeAsync(site, stayer);

        (await Deletion().DeleteAsync(maker, evenLastOwner: false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        var link = await GrantAsync(site, kept.GuestLink);
        link.RevokedAt.Should().BeNull();
        link.CreatedBySubjectId.Should().Be(stayer);
        (await GuestLinks(site).ValidateSessionAsync(kept.GuestLink, Ct)).Should().NotBeNull();
        (await GrantAsync(site, kept.DeviceCredential)).ExpiresAt.Should().BeNull();
        await using var db = Context(site.Tenant);
        (await db.AlertRuleChannels.AsNoTracking().SingleAsync(c => c.AlertRuleId == kept.Rule)).StopsAt.Should().BeNull();
        (await db.MemberInvites.AsNoTracking().SingleAsync(i => i.Id == kept.Invite)).RevokedAt.Should().BeNull();
    }
}
