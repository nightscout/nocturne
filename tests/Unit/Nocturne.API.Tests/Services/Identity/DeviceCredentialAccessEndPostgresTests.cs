using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Hubs;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Middleware.Handlers;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Connectors;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Migration;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Tests.Migration;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using AuthRoleModel = Nocturne.Core.Models.Authorization.Role;
using LegacySubject = Nocturne.Core.Models.Subject;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// A device credential, held by the tenant's device subject, made by a member whose access ends
/// stops on that date, on PostgreSQL under the runtime role and Row Level Security: both mints and a
/// Nightscout import stamp it, both handlers and the hub sweep refuse it after the date, a shorter end
/// date or the removal of such a member lowers it in the same commit, and only a later keep removes it.
/// </summary>
[Trait("Category", "Integration")]
public class DeviceCredentialAccessEndPostgresTests(DeviceCredentialAccessEndPostgresTests.Database database)
    : IClassFixture<DeviceCredentialAccessEndPostgresTests.Database>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("device_credential_access_end");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private sealed record Seeded(Guid Tenant, Guid Maker, SuppliedContextFactory Contexts);

    /// <summary>A tenant with its seed roles and a caretaker, the maker, whose membership ends at <paramref name="accessEndsAt"/>.</summary>
    private async Task<Seeded> SeedAsync(string slug, DateTime? accessEndsAt)
    {
        var tenant = Guid.NewGuid();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'device-credential-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"{slug}-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, CancellationToken.None);
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        var maker = await new SubjectRepository(contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"{slug}-maker"), CancellationToken.None);
        (await members.AddMemberAsync(new NewTenantMember(tenant, maker, [caretaker], AccessEndsAt: accessEndsAt) { How = MembershipJoinHow.Direct, By = null }, CancellationToken.None))
            .Should().BeTrue();
        return new Seeded(tenant, maker, contexts);
    }

    private static CredentialMaker MakerOf(Seeded seeded, DateTime? accessEndsAt) => new(seeded.Maker, accessEndsAt);

    /// <summary><paramref name="maker"/> saving as a caller who may change the site's settings, so a save adopts.</summary>
    private static ConnectorSecretSaver SettingsHolder(CredentialMaker maker) =>
        new(maker, ResolvedScopes(RoleSeeds.Admin), HistoryClamped: false);

    private static IReadOnlySet<string> ResolvedScopes(string role) =>
        MemberScopeResolver.Resolve(new HashSet<string>(RoleSeeds.Permissions[role]), AuthType.SessionCookie, new HashSet<string>());

    private static Mock<ISecretEncryptionService> Encryption()
    {
        var encryption = new Mock<ISecretEncryptionService>();
        encryption.Setup(e => e.IsConfigured).Returns(true);
        encryption
            .Setup(e => e.EncryptSecrets(It.IsAny<Dictionary<string, string>>()))
            .Returns<Dictionary<string, string>>(d => d);
        return encryption;
    }

    /// <summary>Saves <paramref name="secret"/> as the Nightscout connector's API secret, as <paramref name="saver"/>, at <paramref name="at"/>.</summary>
    private async Task<ConnectorSecretsSaveResult> SaveSecretAsync(
        Seeded seeded, string secret, ConnectorSecretSaver saver, DateTimeOffset at)
    {
        var service = new ConnectorConfigurationService(
            new ConnectorConfigurationRepository(seeded.Contexts),
            new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off),
            new OAuthGrantRepository(seeded.Contexts),
            Encryption().Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IAuditContext>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(),
            Enumerable.Empty<IConnectorCacheInvalidator>(),
            new FakeTimeProvider(at),
            NullLogger<ConnectorConfigurationService>.Instance);

        return await service.SaveSecretsAsync(
            "Nightscout", new Dictionary<string, string> { ["ApiSecret"] = secret }, modifiedBy: null, saver: saver);
    }

    /// <summary>Saves <paramref name="secret"/> as <paramref name="maker"/> at <see cref="Now"/> and returns the grant it adopted.</summary>
    private async Task<Guid> AdoptSecretAsync(Seeded seeded, string secret, CredentialMaker maker)
    {
        (await SaveSecretAsync(seeded, secret, SettingsHolder(maker), Now)).SecretAlreadyInUse.Should().BeFalse();
        return (await SecretGrantsAsync(seeded, secret)).Last();
    }

    /// <summary>The grants that hold <paramref name="secret"/>, oldest first.</summary>
    private async Task<List<Guid>> SecretGrantsAsync(Seeded seeded, string secret)
    {
        await using var db = Context(seeded.Tenant);
        return await db.OAuthGrants.AsNoTracking()
            .Where(g => g.LegacySecretHash == HashUtils.Sha1Hex(secret))
            .OrderBy(g => g.Id)
            .Select(g => g.Id)
            .ToListAsync();
    }

    /// <summary>Mints a Nightscout API token through the v2 subject create, as <paramref name="maker"/>.</summary>
    private async Task<(Guid Id, string Token)> MintTokenAsync(Seeded seeded, CredentialMaker maker)
    {
        var roles = new Mock<IRoleService>();
        roles.Setup(r => r.GetAllRolesAsync()).ReturnsAsync(
        [
            new AuthRoleModel { Id = Guid.NewGuid(), Name = "readable", Permissions = ["*:*:read"] },
        ]);
        var service = new AuthorizationService(
            new ConfigurationBuilder().Build(),
            NullLogger<AuthorizationService>.Instance,
            Mock.Of<ISubjectService>(),
            roles.Object,
            new DirectGrantService(
                seeded.Contexts, Mock.Of<IAuthAuditService>(), Revocation(Sessions()), TimeProvider.System, NullLogger<DirectGrantService>.Instance),
            new OAuthGrantRepository(seeded.Contexts),
            Mock.Of<IJwtService>(),
            new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off),
            Mock.Of<ICategoryReadContext>(),
            new FakeTimeProvider(Now));

        var created = await service.CreateSubjectAsync(new LegacySubject { Name = "Camp uploader", Roles = ["readable"] }, maker);
        return (Guid.Parse(created.Id!), created.AccessToken!);
    }

    private async Task<OAuthGrantRecord> GrantAsync(Seeded seeded, Guid id) =>
        (await new OAuthGrantRepository(seeded.Contexts).GetAsync(id, CancellationToken.None))!;

    private async Task<Guid> DeviceSubjectAsync(Seeded seeded) =>
        (await new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off).FindDeviceSubjectAsync(CancellationToken.None))!.Value;

    private static GuestSessionCacheService Sessions() => new(new MemoryCache(new MemoryCacheOptions()));

    private static GrantRevocationService Revocation(GuestSessionCacheService sessions) => new(sessions);

    private async Task<List<(string EventType, Guid? Target, string Details)>> DeviceCredentialAuditAsync(Seeded seeded)
    {
        await using var db = Context(seeded.Tenant);
        return (await db.AuthAuditLog.AsNoTracking()
                .Where(r => r.TenantId == seeded.Tenant
                            && (r.EventType == AuthAuditEventType.DeviceCredentialCapped
                                || r.EventType == AuthAuditEventType.DeviceCredentialKept))
                .OrderBy(r => r.Id)
                .Select(r => new { r.EventType, r.TargetSubjectId, r.DetailsJson })
                .ToListAsync())
            .Select(r => (r.EventType, r.TargetSubjectId, r.DetailsJson ?? string.Empty))
            .ToList();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAdoptedSecretIsStampedWithItsMakerAndStopsWhenTheirAccessEnds(bool makerAccessEnds)
    {
        DateTime? end = makerAccessEnds ? Now.UtcDateTime.AddDays(3) : null;
        var seeded = await SeedAsync("adopt", end);

        var grant = await GrantAsync(seeded, await AdoptSecretAsync(seeded, $"secret-{Guid.NewGuid():N}", MakerOf(seeded, end)));

        grant.SubjectId.Should().Be(await DeviceSubjectAsync(seeded));
        grant.CreatedBySubjectId.Should().Be(seeded.Maker);
        grant.ExpiresAt.Should().Be(end);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALegacyTokenIsStampedWithItsMakerAndStopsWhenTheirAccessEnds(bool makerAccessEnds)
    {
        DateTime? end = makerAccessEnds ? Now.UtcDateTime.AddDays(3) : null;
        var seeded = await SeedAsync("token", end);

        var grant = await GrantAsync(seeded, (await MintTokenAsync(seeded, MakerOf(seeded, end))).Id);

        grant.SubjectId.Should().Be(await DeviceSubjectAsync(seeded));
        grant.CreatedBySubjectId.Should().Be(seeded.Maker);
        grant.ExpiresAt.Should().Be(end);
    }

    private ApiKeyHandler ApiKeys(Seeded seeded, TimeProvider clock) =>
        new(new OAuthGrantRepository(seeded.Contexts), clock, new LastUsedClaims(clock), NullLogger<ApiKeyHandler>.Instance);

    private DirectGrantTokenHandler Bearers(Seeded seeded, TimeProvider clock) =>
        new(new OAuthGrantRepository(seeded.Contexts), clock, new LastUsedClaims(clock), NullLogger<DirectGrantTokenHandler>.Instance);

    private static DefaultHttpContext Request(Seeded seeded)
    {
        var context = new DefaultHttpContext();
        context.Items["TenantContext"] = new TenantContext(seeded.Tenant, "device", "Device", true, IsDemo: false);
        return context;
    }

    private async Task<bool> SecretAuthenticatesAsync(Seeded seeded, string secret, DateTimeOffset at)
    {
        var context = Request(seeded);
        context.Request.Headers["api-secret"] = HashUtils.Sha1Hex(secret);
        return (await ApiKeys(seeded, new FakeTimeProvider(at)).AuthenticateAsync(context)).Succeeded;
    }

    private async Task<bool> TokenAuthenticatesAsync(Seeded seeded, string token, DateTimeOffset at)
    {
        var context = Request(seeded);
        context.Request.Headers.Authorization = $"Bearer {token}";
        return (await Bearers(seeded, new FakeTimeProvider(at)).AuthenticateAsync(context)).Succeeded;
    }

    [Fact]
    public async Task EachDeviceCredentialAuthenticatesOverHttpUntilItStopsAndIsRefusedAfter()
    {
        var end = Now.UtcDateTime.AddHours(1);
        var seeded = await SeedAsync("http", end);
        var secret = $"secret-{Guid.NewGuid():N}";
        await AdoptSecretAsync(seeded, secret, MakerOf(seeded, end));
        var (_, token) = await MintTokenAsync(seeded, MakerOf(seeded, end));

        (await SecretAuthenticatesAsync(seeded, secret, Now)).Should().BeTrue();
        (await TokenAuthenticatesAsync(seeded, token, Now)).Should().BeTrue();

        (await SecretAuthenticatesAsync(seeded, secret, end)).Should().BeFalse("the expiry instant itself does not authenticate");
        (await TokenAuthenticatesAsync(seeded, token, end)).Should().BeFalse();
    }

    private ServiceProvider HubServices(Seeded seeded, Guid deviceSubject)
    {
        var members = new Mock<ITenantMemberService>();
        members
            .Setup(m => m.GetMemberAccessAsync(deviceSubject, seeded.Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantMemberAccess(new HashSet<string> { Scope.FullAccess }, false, null));
        return new ServiceCollection()
            .AddSingleton<INocturneDbContextFactory>(seeded.Contexts)
            .AddSingleton<ISubjectRepository>(new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off))
            .AddSingleton<ITenantRepository>(new TenantRepository(seeded.Contexts))
            .AddSingleton<IOAuthGrantRepository>(new OAuthGrantRepository(seeded.Contexts))
            .AddSingleton<IFirstPartyTokenRepository>(new EfFirstPartyTokenRepository(seeded.Contexts))
            .AddSingleton(members.Object)
            .AddSingleton(Mock.Of<IGuestLinkService>())
            .AddSingleton(Mock.Of<ITenantOverviewService>())
            .AddSingleton(Mock.Of<IOAuthTokenRevocationCache>())
            .AddScoped<ITenantAccessor, HttpContextTenantAccessor>()
            .BuildServiceProvider();
    }

    [Theory]
    [InlineData(AuthType.ApiKey)]
    [InlineData(AuthType.DirectGrant)]
    public async Task EachDeviceCredentialKeepsItsHubConnectionUntilItStopsAndLosesItAtTheSweepAfter(AuthType authType)
    {
        var end = Now.UtcDateTime.AddHours(1);
        var seeded = await SeedAsync("hub", end);
        var grantId = authType == AuthType.ApiKey
            ? await AdoptSecretAsync(seeded, $"secret-{Guid.NewGuid():N}", MakerOf(seeded, end))
            : (await MintTokenAsync(seeded, MakerOf(seeded, end))).Id;
        var deviceSubject = await DeviceSubjectAsync(seeded);
        var tenant = new TenantContext(seeded.Tenant, "device", "Device", IsActive: true, IsDemo: false);
        await using var services = HubServices(seeded, deviceSubject);
        var credential = new HubAuthorization(seeded.Tenant, new HashSet<string>(), HubCredentialKind.Subject, deviceSubject, HistoryClamped: false)
        {
            Source = new HubCredentialSource(
                authType, deviceSubject, grantId, Scope.Normalize([Scope.GlucoseRead]), LimitTo24Hours: false, ExpiresAt: null),
        };

        var admitted = await new HubCredentialVerifier(services, new FakeTimeProvider(Now)).ProveAsync(credential, tenant);
        admitted.Should().NotBeNull();
        admitted!.IsCoveredBy(await new HubCredentialVerifier(services, new FakeTimeProvider(Now.AddMinutes(59)))
            .ProveAsync(admitted, tenant)).Should().BeTrue();

        admitted.IsCoveredBy(await new HubCredentialVerifier(services, new FakeTimeProvider(end))
            .ProveAsync(admitted, tenant)).Should().BeFalse();
    }

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

    private async Task<Guid> MemberIdAsync(Seeded seeded) =>
        (await new TenantMembershipRepository(seeded.Contexts, AccountLinkRoutes.Off)
            .FindMemberAsync(seeded.Tenant, seeded.Maker, CancellationToken.None))!.Id;

    private async Task<OwnerGuardedChange> SetEndAsync(Seeded seeded, DateTime? end, INocturneDbContextFactory? contexts = null) =>
        await Service(contexts ?? seeded.Contexts, Sessions())
            .SetMemberAccessEndAsync(seeded.Tenant, await MemberIdAsync(seeded), end, Now.UtcDateTime);

    [Fact]
    public async Task SettingAnEndDateCapsTheMakersDeviceCredentialsAndAuditsEachCap()
    {
        var seeded = await SeedAsync("cap", accessEndsAt: null);
        var secret = await AdoptSecretAsync(seeded, $"secret-{Guid.NewGuid():N}", MakerOf(seeded, null));
        var token = (await MintTokenAsync(seeded, MakerOf(seeded, null))).Id;
        var end = Now.UtcDateTime.AddDays(2);

        (await SetEndAsync(seeded, end)).Should().Be(OwnerGuardedChange.Changed);

        (await GrantAsync(seeded, secret)).ExpiresAt.Should().Be(end);
        (await GrantAsync(seeded, token)).ExpiresAt.Should().Be(end);
        var audit = await DeviceCredentialAuditAsync(seeded);
        audit.Should().HaveCount(2).And.OnlyContain(r =>
            r.EventType == AuthAuditEventType.DeviceCredentialCapped && r.Target == seeded.Maker);
        using var details = System.Text.Json.JsonDocument.Parse(audit[0].Details);
        details.RootElement.GetProperty("grant").GetString().Should().BeOneOf(secret.ToString(), token.ToString());
        details.RootElement.GetProperty("expires_at").GetDateTime().ToUniversalTime().Should().Be(end);
    }

    [Fact]
    public async Task ShorteningCapsAgainAndALaterOrClearedDateRaisesNothing()
    {
        var seeded = await SeedAsync("shorten", Now.UtcDateTime.AddDays(5));
        var token = (await MintTokenAsync(seeded, MakerOf(seeded, Now.UtcDateTime.AddDays(5)))).Id;

        await SetEndAsync(seeded, Now.UtcDateTime.AddDays(2));
        (await GrantAsync(seeded, token)).ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(2));

        await SetEndAsync(seeded, Now.UtcDateTime.AddDays(9));
        (await GrantAsync(seeded, token)).ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(2), "a later date is not a cap");

        await SetEndAsync(seeded, null);
        (await GrantAsync(seeded, token)).ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(2), "clearing the date lengthens nothing");
        (await DeviceCredentialAuditAsync(seeded)).Should().ContainSingle();
    }

    [Fact]
    public async Task ACapLeavesTheDeviceCredentialsAnotherMemberMade()
    {
        var seeded = await SeedAsync("cap-scope", accessEndsAt: null);
        var other = await new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject("cap-scope-other"), CancellationToken.None);
        var theirs = (await MintTokenAsync(seeded, new CredentialMaker(other, null))).Id;

        await SetEndAsync(seeded, Now.UtcDateTime.AddDays(1));

        (await GrantAsync(seeded, theirs)).ExpiresAt.Should().BeNull();
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
    public async Task ADateChangeThatRollsBackCapsNothing()
    {
        var seeded = await SeedAsync("rollback", accessEndsAt: null);
        var token = (await MintTokenAsync(seeded, MakerOf(seeded, null))).Id;

        await FluentActions.Awaiting(() => SetEndAsync(
                seeded, Now.UtcDateTime.AddDays(1), new SuppliedContextFactory(() => ContextRefusingGrantSaves(seeded.Tenant))))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("grant save refused");

        (await GrantAsync(seeded, token)).ExpiresAt.Should().BeNull();
        (await DeviceCredentialAuditAsync(seeded)).Should().BeEmpty();
        await using var db = Context(seeded.Tenant);
        (await db.TenantMembers.AsNoTracking().SingleAsync(m => m.SubjectId == seeded.Maker)).AccessEndsAt.Should().BeNull();
    }

    private async Task RemoveAsync(Seeded seeded)
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(seeded.Contexts, seeded.Tenant);
        var members = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);
        var member = (await members.FindMemberAsync(seeded.Tenant, seeded.Maker, CancellationToken.None))!;
        var (outcome, _) = await unit.CommitAsync(ct =>
            MembershipRemoval.StageAsync(unit, members, Revocation(Sessions()), seeded.Tenant, member, MembershipEndHow.Removed, Now.UtcDateTime, ct));
        outcome.Should().Be(OwnerGuardedChange.Changed);
    }

    [Fact]
    public async Task RemovingAMemberWithAnEndDateStopsTheirDeviceCredentialsAtOnce()
    {
        var end = Now.UtcDateTime.AddDays(4);
        var seeded = await SeedAsync("remove-ending", end);
        var secret = $"secret-{Guid.NewGuid():N}";
        var adopted = await AdoptSecretAsync(seeded, secret, MakerOf(seeded, end));
        var (minted, token) = await MintTokenAsync(seeded, MakerOf(seeded, end));

        await RemoveAsync(seeded);

        (await GrantAsync(seeded, adopted)).ExpiresAt.Should().Be(Now.UtcDateTime);
        (await GrantAsync(seeded, minted)).ExpiresAt.Should().Be(Now.UtcDateTime);
        (await GrantAsync(seeded, minted)).RevokedAt.Should().BeNull("a device credential is stopped, not revoked");
        (await SecretAuthenticatesAsync(seeded, secret, Now)).Should().BeFalse();
        (await TokenAuthenticatesAsync(seeded, token, Now)).Should().BeFalse();
        (await DeviceCredentialAuditAsync(seeded)).Should().HaveCount(2)
            .And.OnlyContain(r => r.EventType == AuthAuditEventType.DeviceCredentialCapped);
    }

    [Fact]
    public async Task RemovingAMemberWithNoEndDateLeavesTheirDeviceCredentials()
    {
        var seeded = await SeedAsync("remove-open", accessEndsAt: null);
        var secret = $"secret-{Guid.NewGuid():N}";
        var adopted = await AdoptSecretAsync(seeded, secret, MakerOf(seeded, null));

        await RemoveAsync(seeded);

        (await GrantAsync(seeded, adopted)).ExpiresAt.Should().BeNull();
        (await SecretAuthenticatesAsync(seeded, secret, Now)).Should().BeTrue();
        (await DeviceCredentialAuditAsync(seeded)).Should().BeEmpty();
    }

    [Fact]
    public async Task KeepingADeviceCredentialRemovesItsExpiryAndIsAudited()
    {
        var end = Now.UtcDateTime.AddDays(1);
        var seeded = await SeedAsync("keep", end);
        var (minted, token) = await MintTokenAsync(seeded, MakerOf(seeded, end));

        (await new DirectGrantService(
                    seeded.Contexts, Mock.Of<IAuthAuditService>(), Revocation(Sessions()), TimeProvider.System, NullLogger<DirectGrantService>.Instance)
                .KeepDeviceCredentialAsync(minted))
            .Should().BeTrue();

        (await GrantAsync(seeded, minted)).ExpiresAt.Should().BeNull();
        (await GrantAsync(seeded, minted)).CreatedBySubjectId.Should().BeNull();
        (await TokenAuthenticatesAsync(seeded, token, Now.AddDays(30))).Should().BeTrue();
        var kept = (await DeviceCredentialAuditAsync(seeded)).Should().ContainSingle().Subject;
        kept.EventType.Should().Be(AuthAuditEventType.DeviceCredentialKept);
        kept.Target.Should().Be(seeded.Maker);
    }

    [Fact]
    public async Task KeepingIsRefusedForACredentialThatIsNotADeviceCredential()
    {
        var seeded = await SeedAsync("keep-own", accessEndsAt: null);
        await MintTokenAsync(seeded, MakerOf(seeded, null));
        var expiresAt = Now.UtcDateTime.AddDays(1);
        var own = await new OAuthGrantRepository(seeded.Contexts).CreateAsync(
            new NewOAuthGrant(null, seeded.Maker, OAuthGrantTypes.Direct, [Scope.GlucoseRead], "mine", Guid.NewGuid().ToString("N"), null, expiresAt, false),
            CancellationToken.None);

        (await new OAuthGrantRepository(seeded.Contexts).KeepDeviceCredentialAsync(own, CancellationToken.None))
            .Should().BeFalse();

        (await GrantAsync(seeded, own)).ExpiresAt.Should().Be(expiresAt);
        (await DeviceCredentialAuditAsync(seeded)).Should().BeEmpty();
    }

    private DirectGrantService DirectGrants(Seeded seeded) =>
        new(seeded.Contexts, Mock.Of<IAuthAuditService>(), Revocation(Sessions()), TimeProvider.System, NullLogger<DirectGrantService>.Instance);

    [Fact]
    public async Task AKeptCredentialStaysKeptThroughEveryLaterChangeToItsMaker()
    {
        var end = Now.UtcDateTime.AddDays(5);
        var seeded = await SeedAsync("keep-lasts", end);
        var (minted, _) = await MintTokenAsync(seeded, MakerOf(seeded, end));
        (await DirectGrants(seeded).KeepDeviceCredentialAsync(minted)).Should().BeTrue();

        await SetEndAsync(seeded, Now.UtcDateTime.AddDays(1));
        (await GrantAsync(seeded, minted)).ExpiresAt.Should().BeNull("a shorter end date no longer reaches a kept credential");

        await RemoveAsync(seeded);
        (await GrantAsync(seeded, minted)).ExpiresAt.Should().BeNull("removing the maker no longer reaches it");

        var members = new TenantMembershipRepository(seeded.Contexts, AccountLinkRoutes.Off);
        var caretaker = (await members.FindRoleIdBySlugAsync(seeded.Tenant, RoleSeeds.Caretaker, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(
                new NewTenantMember(seeded.Tenant, seeded.Maker, [caretaker], AccessEndsAt: Now.UtcDateTime.AddDays(3)) { How = MembershipJoinHow.Direct, By = null },
                CancellationToken.None))
            .Should().BeTrue();
        await SetEndAsync(seeded, Now.UtcDateTime.AddDays(2));
        (await GrantAsync(seeded, minted)).ExpiresAt.Should().BeNull("a rejoined maker's end date no longer reaches it");
        (await DeviceCredentialAuditAsync(seeded)).Should().ContainSingle()
            .Which.EventType.Should().Be(AuthAuditEventType.DeviceCredentialKept);
    }

    [Fact]
    public async Task KeepingIsRefusedForADeviceGrantThatCarriesNoMakersStamp()
    {
        var seeded = await SeedAsync("keep-admin", accessEndsAt: null);
        await MintTokenAsync(seeded, MakerOf(seeded, null));
        var expiresAt = Now.UtcDateTime.AddDays(1);
        var admin = await DirectGrants(seeded).CreateAsync(
            seeded.Tenant, await DeviceSubjectAsync(seeded), "Admin-made", [Scope.GlucoseRead], expiresAt, false,
            null, null, createdBySubjectId: null);

        (await DirectGrants(seeded).KeepDeviceCredentialAsync(admin.Response!.Id)).Should().BeFalse();

        (await GrantAsync(seeded, admin.Response.Id)).ExpiresAt.Should().Be(expiresAt);
        (await DeviceCredentialAuditAsync(seeded)).Should().BeEmpty();
    }

    private const string JwtSecret = "device-credential-test-secret-key-32+chars";

    private static JwtService Jwt(TimeProvider clock) => new(
        Options.Create(new JwtOptions
        {
            SecretKey = JwtSecret,
            Issuer = "nocturne",
            Audience = "nocturne-api",
            AccessTokenLifetimeMinutes = 15,
        }),
        NullLogger<JwtService>.Instance,
        clock);

    /// <summary>Exchanges <paramref name="token"/> for a JWT through the v2 authorization request, at the clock's time.</summary>
    private async Task<AuthorizationResponse> ExchangeAsync(Seeded seeded, string token, TimeProvider clock)
    {
        var subjects = new Mock<ISubjectService>();
        subjects
            .Setup(s => s.GetSubjectByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => new Nocturne.Core.Models.Authorization.Subject { Id = id, Name = "Devices", IsActive = true });
        var service = new AuthorizationService(
            new ConfigurationBuilder().Build(),
            NullLogger<AuthorizationService>.Instance,
            subjects.Object,
            Mock.Of<IRoleService>(),
            DirectGrants(seeded),
            new OAuthGrantRepository(seeded.Contexts),
            Jwt(clock),
            new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off),
            Mock.Of<ICategoryReadContext>(),
            clock);
        return (await service.GenerateJwtFromAccessTokenAsync(token))!;
    }

    /// <summary>Whether <paramref name="jwt"/> authenticates over HTTP through the access token handler at the clock's time.</summary>
    private async Task<bool> JwtAuthenticatesAsync(Seeded seeded, string jwt, TimeProvider clock)
    {
        var revocations = new Mock<IOAuthTokenRevocationCache>();
        revocations.Setup(c => c.IsRevokedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var services = new ServiceCollection()
            .AddSingleton<IJwtService>(Jwt(clock))
            .AddSingleton(revocations.Object)
            .AddSingleton<IOAuthGrantService>(new OAuthGrantService(
                seeded.Contexts, new OAuthGrantRepository(seeded.Contexts), Mock.Of<IOAuthClientService>(), Sessions(),
                Revocation(Sessions()), new GrantConsentService(NullLogger<GrantConsentService>.Instance), clock, NullLogger<OAuthGrantService>.Instance))
            .AddScoped<IJwtCredentialValidator, JwtCredentialValidator>()
            .AddSingleton<ILogger<JwtCredentialValidator>>(NullLogger<JwtCredentialValidator>.Instance)
            .BuildServiceProvider();
        var handler = new OAuthAccessTokenHandler(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OAuthAccessTokenHandler>.Instance);
        var context = Request(seeded);
        context.Request.Headers.Authorization = $"Bearer {jwt}";
        return (await handler.AuthenticateAsync(context)).Succeeded;
    }

    [Fact]
    public async Task AJwtExchangedNearTheStopEndsWithTheCredentialAndIsRefusedAfter()
    {
        var end = Now.UtcDateTime.AddMinutes(20);
        var seeded = await SeedAsync("jwt-near", end);
        var (_, token) = await MintTokenAsync(seeded, MakerOf(seeded, end));
        var clock = new FakeTimeProvider(Now);

        var exchanged = await ExchangeAsync(seeded, token, clock);

        exchanged.Exp.Should().Be(new DateTimeOffset(end, TimeSpan.Zero).ToUnixTimeSeconds(),
            "a JWT ends no later than the grant it was exchanged from");
        (await JwtAuthenticatesAsync(seeded, exchanged.Token!, clock)).Should().BeTrue();

        clock.SetUtcNow(new DateTimeOffset(end, TimeSpan.Zero).AddMinutes(1));
        (await JwtAuthenticatesAsync(seeded, exchanged.Token!, clock)).Should().BeFalse();
    }

    [Fact]
    public async Task AJwtOfACappedCredentialIsRefusedOverHttpOnceTheCapPasses()
    {
        var seeded = await SeedAsync("jwt-capped", accessEndsAt: null);
        var (_, token) = await MintTokenAsync(seeded, MakerOf(seeded, null));
        var clock = new FakeTimeProvider(Now);
        var exchanged = await ExchangeAsync(seeded, token, clock);
        exchanged.Exp.Should().Be(Now.AddHours(1).ToUnixTimeSeconds());

        await SetEndAsync(seeded, Now.UtcDateTime.AddMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(20));

        (await JwtAuthenticatesAsync(seeded, exchanged.Token!, clock)).Should().BeFalse(
            "the JWT's own expiry has not come, but its grant has stopped");
    }

    private DefaultHttpContext Presenting(Seeded seeded, Guid subject, Guid grantId)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IOAuthGrantRepository>(new OAuthGrantRepository(seeded.Contexts))
                .BuildServiceProvider(),
        };
        context.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.ApiKey,
            SubjectId = subject,
            TenantId = seeded.Tenant,
            TokenId = grantId,
        };
        return context;
    }

    [Fact]
    public async Task ATokenPresentedToMakeAnotherPassesOnItsMakerAndItsStop()
    {
        var end = Now.UtcDateTime.AddDays(3);
        var seeded = await SeedAsync("launder", end);
        var (first, _) = await MintTokenAsync(seeded, MakerOf(seeded, end));

        var maker = await Presenting(seeded, await DeviceSubjectAsync(seeded), first).CredentialMakerAsync(CancellationToken.None);

        maker.Should().Be(new CredentialMaker(seeded.Maker, end));
        var (second, _) = await MintTokenAsync(seeded, maker!);
        var made = await GrantAsync(seeded, second);
        made.ExpiresAt.Should().Be(end, "a credential cannot make one that outlasts itself");
        made.CreatedBySubjectId.Should().Be(seeded.Maker);

        await SetEndAsync(seeded, Now.UtcDateTime.AddDays(1));
        (await GrantAsync(seeded, second)).ExpiresAt.Should().Be(Now.UtcDateTime.AddDays(1),
            "the member's shorter date reaches what their credential made");
    }

    [Fact]
    public async Task AMembersOwnKeyThatStopsSoonerThanTheirAccessHoldsWhatItMakesToItsOwnStop()
    {
        var seeded = await SeedAsync("own-key", Now.UtcDateTime.AddDays(3));
        var keyExpires = Now.UtcDateTime.AddDays(1);
        var key = await new OAuthGrantRepository(seeded.Contexts).CreateAsync(
            new NewOAuthGrant(null, seeded.Maker, OAuthGrantTypes.Direct, [Scope.FullAccess], "mine", Guid.NewGuid().ToString("N"), null, keyExpires, false),
            CancellationToken.None);
        var context = Presenting(seeded, seeded.Maker, key);
        new RequestMembership(
                seeded.Maker, seeded.Tenant,
                new MembershipFacts(Guid.NewGuid(), new TenantMemberAccess(new HashSet<string> { Scope.FullAccess }, false, Now.UtcDateTime.AddDays(3)), new TenantMemberAccess(new HashSet<string> { Scope.FullAccess }, false, Now.UtcDateTime.AddDays(3)), null))
            .StoreOn(context);

        (await context.CredentialMakerAsync(CancellationToken.None)).Should().Be(new CredentialMaker(seeded.Maker, keyExpires));
    }

    [Fact]
    public async Task ASecretSavedAgainAfterItsCredentialStoppedIsAdoptedUnderTheNewSaversStamp()
    {
        var seeded = await SeedAsync("readopt-dated", Now.UtcDateTime.AddHours(1));
        var secret = $"secret-{Guid.NewGuid():N}";
        var first = await AdoptSecretAsync(seeded, secret, MakerOf(seeded, Now.UtcDateTime.AddHours(1)));
        var extended = Now.UtcDateTime.AddDays(5);

        var result = await SaveSecretAsync(seeded, secret, SettingsHolder(MakerOf(seeded, extended)), Now.AddHours(2));

        result.SecretAlreadyInUse.Should().BeFalse();
        var grants = await SecretGrantsAsync(seeded, secret);
        grants.Should().HaveCount(2).And.StartWith(first);
        (await GrantAsync(seeded, grants[1])).ExpiresAt.Should().Be(extended);
        (await SecretAuthenticatesAsync(seeded, secret, Now.AddHours(3))).Should().BeTrue();
    }

    [Fact]
    public async Task ASecretSavedAgainAfterItStoppedByAMemberWithNoEndDoesNotStop()
    {
        var seeded = await SeedAsync("readopt-open", Now.UtcDateTime.AddHours(1));
        var secret = $"secret-{Guid.NewGuid():N}";
        await AdoptSecretAsync(seeded, secret, MakerOf(seeded, Now.UtcDateTime.AddHours(1)));
        var owner = await new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject("readopt-owner"), CancellationToken.None);

        (await SaveSecretAsync(seeded, secret, SettingsHolder(new CredentialMaker(owner, null)), Now.AddHours(2)))
            .SecretAlreadyInUse.Should().BeFalse();

        var latest = await GrantAsync(seeded, (await SecretGrantsAsync(seeded, secret)).Last());
        latest.ExpiresAt.Should().BeNull();
        latest.CreatedBySubjectId.Should().Be(owner);
    }

    [Fact]
    public async Task ASecretSavedAgainWhileItsCredentialIsLiveAdoptsNothingAndSaysWhenThatOneStops()
    {
        var stops = Now.UtcDateTime.AddHours(1);
        var seeded = await SeedAsync("readopt-live", stops);
        var secret = $"secret-{Guid.NewGuid():N}";
        await AdoptSecretAsync(seeded, secret, MakerOf(seeded, stops));

        var result = await SaveSecretAsync(seeded, secret, SettingsHolder(MakerOf(seeded, Now.UtcDateTime.AddDays(5))), Now.AddMinutes(30));

        result.SecretAlreadyInUse.Should().BeTrue();
        result.SecretInUseStopsAt.Should().Be(stops);
        (await SecretGrantsAsync(seeded, secret)).Should().ContainSingle();
    }

    [Fact]
    public async Task ACaretakerSavingAnApiSecretMakesNoCredential()
    {
        var seeded = await SeedAsync("caretaker-save", null);
        var secret = $"secret-{Guid.NewGuid():N}";

        var result = await SaveSecretAsync(
            seeded, secret, new ConnectorSecretSaver(MakerOf(seeded, null), ResolvedScopes(RoleSeeds.Caretaker), HistoryClamped: false), Now);

        result.NotAdopted.Should().BeTrue();
        result.SecretAlreadyInUse.Should().BeFalse();
        (await SecretGrantsAsync(seeded, secret)).Should().BeEmpty();
        (await SecretAuthenticatesAsync(seeded, secret, Now.AddMinutes(1))).Should().BeFalse();
    }

    [Fact]
    public async Task ACaretakerSavingOverAnAdoptedSecretLeavesThatCredentialExactlyAsItWas()
    {
        var seeded = await SeedAsync("caretaker-over", null);
        var ownersSecret = $"secret-{Guid.NewGuid():N}";
        var adopted = await AdoptSecretAsync(seeded, ownersSecret, MakerOf(seeded, null));
        var before = await GrantRowAsync(seeded, adopted);
        var caretakersSecret = $"secret-{Guid.NewGuid():N}";

        var result = await SaveSecretAsync(
            seeded, caretakersSecret,
            new ConnectorSecretSaver(MakerOf(seeded, null), ResolvedScopes(RoleSeeds.Caretaker), HistoryClamped: false), Now.AddHours(1));

        result.NotAdopted.Should().BeTrue();
        (await GrantRowAsync(seeded, adopted)).Should().Be(before);
        (await SecretGrantsAsync(seeded, caretakersSecret)).Should().BeEmpty();
        (await SecretAuthenticatesAsync(seeded, ownersSecret, Now.AddHours(2))).Should().BeTrue();
    }

    [Fact]
    public async Task ASettingsHolderWhoseReadsAreLimitedTo24HoursMakesNoCredential()
    {
        var seeded = await SeedAsync("clamped-save", null);
        var secret = $"secret-{Guid.NewGuid():N}";

        var result = await SaveSecretAsync(
            seeded, secret, new ConnectorSecretSaver(MakerOf(seeded, null), ResolvedScopes(RoleSeeds.Admin), HistoryClamped: true), Now);

        result.NotAdopted.Should().BeTrue();
        (await SecretGrantsAsync(seeded, secret)).Should().BeEmpty();
    }

    private const string ImportedToken = "camp-uploader-0123456789abcdef";

    /// <summary>A Nightscout site with one token, read-only, for an import to adopt; every other path answers 404.</summary>
    private sealed class OneTokenSite : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/authorization/roles" => """[{ "name": "readable", "permissions": ["*:*:read"] }]""",
                "/api/v2/authorization/subjects" => $$"""
                    [{ "_id": "5f1a00000000000000000001", "name": "Camp uploader", "roles": ["readable"], "accessToken": "{{ImportedToken}}" }]
                    """,
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
        }
    }

    private sealed record StartedImport(MigrationJobService Service, TenantContext Tenant, Guid JobId, ServiceProvider Provider);

    /// <summary>Starts an import of <see cref="OneTokenSite"/>'s tokens into the tenant, started by <paramref name="starter"/>.</summary>
    private async Task<StartedImport> StartImportAsync(Seeded seeded, CredentialMaker? starter)
    {
        var provider = MigrationJobHarness.BuildProvider(
            new OneTokenSite(),
            database: o => o.UseTenantInterceptor(database.AppConnectionString));
        var service = MigrationJobHarness.CreateService(provider);
        var tenant = new TenantContext(seeded.Tenant, "device", "Device", IsActive: true, IsDemo: false);
        var job = await service.StartMigrationAsync(
            new StartMigrationRequest
            {
                Mode = MigrationMode.Api,
                NightscoutUrl = "https://example-nightscout.invalid",
                NightscoutApiSecret = "synthetic-secret",
                Collections = ["subjects"],
            },
            tenant,
            starter);
        return new StartedImport(service, tenant, job.Id, provider);
    }

    /// <summary>Runs the import from its record, as the job scheduler does, and returns the grant it adopted.</summary>
    private async Task<OAuthGrantRecord> FinishImportAsync(Seeded seeded, StartedImport import)
    {
        await using (import.Provider)
        {
            await MigrationJobHarness.RunToEndAsync(import.Service, import.Tenant);
            (await import.Service.GetStatusAsync(seeded.Tenant, import.JobId)).State.Should().Be(MigrationJobState.Completed);
        }

        await using var db = Context(seeded.Tenant);
        var adopted = await db.OAuthGrants.AsNoTracking()
            .Where(g => g.TokenHash == HashUtils.Sha256Hex(ImportedToken))
            .Select(g => g.Id)
            .SingleAsync();
        return await GrantAsync(seeded, adopted);
    }

    private async Task<DateTime?> ImportEndAsync(Seeded seeded, Guid jobId)
    {
        await using var db = Context(seeded.Tenant);
        return await db.MigrationRuns.AsNoTracking().Where(r => r.Id == jobId).Select(r => r.StarterAccessEndsAt).SingleAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnImportedTokenIsStampedWithTheStarterAndStopsWhenTheirAccessEnds(bool starterAccessEnds)
    {
        DateTime? end = starterAccessEnds ? Now.UtcDateTime.AddDays(3) : null;
        var seeded = await SeedAsync("import", end);

        var grant = await FinishImportAsync(seeded, await StartImportAsync(seeded, MakerOf(seeded, end)));

        grant.SubjectId.Should().Be(await DeviceSubjectAsync(seeded));
        grant.CreatedBySubjectId.Should().Be(seeded.Maker);
        grant.ExpiresAt.Should().Be(end);
    }

    [Fact]
    public async Task AnImportNobodyStartedAdoptsTokensWithNoMakerAndNoExpiry()
    {
        var seeded = await SeedAsync("import-system", accessEndsAt: null);

        var grant = await FinishImportAsync(seeded, await StartImportAsync(seeded, starter: null));

        grant.CreatedBySubjectId.Should().BeNull();
        grant.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task AResumedImportStampsTheEndReadAtStartNotALaterOne()
    {
        var end = Now.UtcDateTime.AddDays(3);
        var seeded = await SeedAsync("import-resume", end);
        var import = await StartImportAsync(seeded, MakerOf(seeded, end));

        (await SetEndAsync(seeded, Now.UtcDateTime.AddDays(30))).Should().Be(OwnerGuardedChange.Changed);

        (await FinishImportAsync(seeded, import)).ExpiresAt.Should().Be(end, "a later end raises nothing the starter made");
    }

    [Fact]
    public async Task AnEndLoweredWhileTheImportRunsReachesTheTokensItAdoptsAfter()
    {
        var seeded = await SeedAsync("import-lowered", Now.UtcDateTime.AddDays(10));
        var import = await StartImportAsync(seeded, MakerOf(seeded, Now.UtcDateTime.AddDays(10)));
        var lowered = Now.UtcDateTime.AddDays(2);

        (await SetEndAsync(seeded, lowered)).Should().Be(OwnerGuardedChange.Changed);

        (await ImportEndAsync(seeded, import.JobId)).Should().Be(lowered);
        (await FinishImportAsync(seeded, import)).ExpiresAt.Should().Be(lowered);
    }

    [Fact]
    public async Task RemovingTheStarterWhileTheImportRunsStopsTheTokensItAdoptsAfter()
    {
        var seeded = await SeedAsync("import-removed", Now.UtcDateTime.AddDays(10));
        var import = await StartImportAsync(seeded, MakerOf(seeded, Now.UtcDateTime.AddDays(10)));

        await RemoveAsync(seeded);

        (await FinishImportAsync(seeded, import)).ExpiresAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task ACapLeavesTheImportsAnotherMemberStartedAndTheStartersImportsInAnotherTenant()
    {
        var end = Now.UtcDateTime.AddDays(10);
        var seeded = await SeedAsync("import-scope", end);
        var elsewhere = await SeedAsync("import-scope-elsewhere", accessEndsAt: null);
        var other = await new SubjectRepository(seeded.Contexts, AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject("import-scope-other"), CancellationToken.None);
        var theirs = await StartImportAsync(seeded, new CredentialMaker(other, end));
        var startersElsewhere = await StartImportAsync(elsewhere, MakerOf(seeded, end));
        await using var theirsProvider = theirs.Provider;
        await using var elsewhereProvider = startersElsewhere.Provider;

        (await SetEndAsync(seeded, Now.UtcDateTime.AddDays(2))).Should().Be(OwnerGuardedChange.Changed);

        (await ImportEndAsync(seeded, theirs.JobId)).Should().Be(end);
        (await ImportEndAsync(elsewhere, startersElsewhere.JobId)).Should().Be(end);
    }

    [Fact]
    public async Task ACapAfterTheImportLowersTheTokensItAdoptedAndLeavesTheEndedRun()
    {
        var seeded = await SeedAsync("import-capped", Now.UtcDateTime.AddDays(10));
        var import = await StartImportAsync(seeded, MakerOf(seeded, Now.UtcDateTime.AddDays(10)));
        var grant = await FinishImportAsync(seeded, import);
        var lowered = Now.UtcDateTime.AddDays(2);

        await SetEndAsync(seeded, lowered);

        (await GrantAsync(seeded, grant.Id)).ExpiresAt.Should().Be(lowered);
        (await ImportEndAsync(seeded, import.JobId)).Should().Be(Now.UtcDateTime.AddDays(10), "an ended run adopts nothing more");
        (await DeviceCredentialAuditAsync(seeded)).Should().ContainSingle(r =>
            r.EventType == AuthAuditEventType.DeviceCredentialCapped && r.Target == seeded.Maker);
    }

    /// <summary>The stored row of grant <paramref name="id"/>, every column, as PostgreSQL writes it.</summary>
    private async Task<string> GrantRowAsync(Seeded seeded, Guid id)
    {
        await using var conn = new NpgsqlConnection(database.MigratorConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT set_config('app.current_tenant_id', @tenant, false);
            SELECT row_to_json(g)::text FROM oauth_grants g WHERE g.id = @id;
            """;
        cmd.Parameters.Add(new NpgsqlParameter("@tenant", seeded.Tenant.ToString()));
        cmd.Parameters.Add(new NpgsqlParameter("@id", id));
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.NextResultAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return reader.GetString(0);
    }
}
