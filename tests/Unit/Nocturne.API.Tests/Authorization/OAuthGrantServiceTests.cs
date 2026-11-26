using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Models.OAuth;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Unit tests for OAuthGrantService covering grant management,
/// scope updates, and ownership checks.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "OAuth")]
public class OAuthGrantServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly Mock<IOAuthClientService> _mockClientService;
    private readonly Mock<ILogger<OAuthGrantService>> _mockLogger;
    private readonly GuestSessionCacheService _guestSessionCache =
        new(new MemoryCache(new MemoryCacheOptions()));

    private const string TestClientId = "test-client-id";

    private readonly Guid _testTenantId = Guid.CreateVersion7();
    private readonly Guid _testClientEntityId = Guid.CreateVersion7();
    private readonly Guid _ownerSubjectId = Guid.CreateVersion7();
    private static readonly IReadOnlySet<string> FullAccessCeiling = new HashSet<string> { Scope.FullAccess };

    private static readonly GrantScopeBounds FullAccessBounds = new(FullAccessCeiling, FullAccessCeiling);

    public OAuthGrantServiceTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_testTenantId);

        _mockClientService = new Mock<IOAuthClientService>();
        _mockLogger = new Mock<ILogger<OAuthGrantService>>();

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private void SetupDefaultMocks()
    {
        _mockClientService.Setup(c => c.GetClientAsync(
                TestClientId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OAuthClientInfo
            {
                Id = _testClientEntityId,
                ClientId = TestClientId,
                DisplayName = "Test App",
                IsKnown = false,
            });
    }

    private OAuthGrantService CreateService(NocturneDbContext dbContext, TimeProvider? time = null)
    {
        return new OAuthGrantService(
            _db.ContextFactory.AsNocturneFactory(), new OAuthGrantRepository(_db.ContextFactory.AsNocturneFactory()),
            _mockClientService.Object,
            _guestSessionCache,
            new GrantRevocationService(_guestSessionCache),
            new GrantConsentService(NullLogger<GrantConsentService>.Instance),
            time ?? TimeProvider.System,
            _mockLogger.Object);
    }

    /// <summary>
    /// Hands out tenant-pinned contexts over the shared in-memory connection, standing in for the
    /// registered <see cref="IDbContextFactory{TContext}"/>.
    /// </summary>
    private NocturneDbContext CreateDbContext()
    {
        return _db.CreateContext();
    }

    private async Task SeedClientAsync(
        NocturneDbContext db, Guid? id = null, string? clientId = null, bool isKnown = false)
    {
        db.OAuthClients.Add(new OAuthClientEntity
        {
            Id = id ?? _testClientEntityId,
            ClientId = clientId ?? TestClientId,
            DisplayName = "Test App",
            IsKnown = isKnown,
            RedirectUris = "[]",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSubjectAsync(NocturneDbContext db, Guid id, string name, string? email = null)
    {
        var subject = new SubjectEntity { Id = id, Name = name };
        subject.SetEmail(email);
        db.Subjects.Add(subject);
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = db.TenantId,
            SubjectId = id,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedGrantAsync(
        NocturneDbContext db,
        Guid? clientEntityId = null,
        Guid? subjectId = null,
        string grantType = "app",
        List<string>? scopes = null,
        string? label = null,
        DateTime? revokedAt = null)
    {
        var id = Guid.CreateVersion7();
        db.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = id,
            ClientEntityId = clientEntityId ?? _testClientEntityId,
            SubjectId = subjectId ?? _ownerSubjectId,
            GrantType = grantType,
            Scopes = scopes ?? new List<string> { "glucose.read" },
            Label = label,
            RevokedAt = revokedAt,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    /// <summary>A consent the way a device-code approval stages it, committed on its own unit.</summary>
    private async Task<OAuthGrantInfo> StageConsentAsync(string[] scopes, bool approvedByDeviceCode)
    {
        var contexts = _db.ContextFactory.AsNocturneFactory();
        await using var unit = await DbUnitOfWork.OpenAsync(contexts);
        var consent = new GrantConsentService(NullLogger<GrantConsentService>.Instance);
        var staged = await unit.CommitAsync(token => consent.StageAsync(
            unit, _testClientEntityId, _ownerSubjectId, scopes, approvedByDeviceCode: approvedByDeviceCode, ct: token));
        return staged.Grant;
    }

    // ---------------------------------------------------------------
    // ClientIdentity
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(true, false, OAuthClientIdentity.Verified)]
    [InlineData(true, true, OAuthClientIdentity.Unverified)]
    [InlineData(false, false, OAuthClientIdentity.SelfRegistered)]
    [InlineData(false, true, OAuthClientIdentity.SelfRegistered)]
    public async Task OnlyAKnownClientNoDeviceCodeApprovedIsVerified(
        bool isKnown, bool approvedByDeviceCode, OAuthClientIdentity identity)
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db, isKnown: isKnown);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var service = CreateService(db);

        var created = await StageConsentAsync([Scope.GlucoseRead], approvedByDeviceCode);
        var listed = await service.GetGrantsForSubjectAsync(_ownerSubjectId);

        created.ClientIdentity.Should().Be(identity);
        listed.Should().ContainSingle().Which.ClientIdentity.Should().Be(identity);
    }

    [Fact]
    public async Task AGrantADeviceCodeApprovedStaysUnverifiedAfterALaterConsent()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db, isKnown: true);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var service = CreateService(db);

        await StageConsentAsync([Scope.GlucoseRead], approvedByDeviceCode: true);
        var consented = await service.CreateOrUpdateGrantAsync(
            _testClientEntityId, _ownerSubjectId, [Scope.TreatmentsRead]);

        consented.ClientIdentity.Should().Be(OAuthClientIdentity.Unverified);
    }

    // ---------------------------------------------------------------
    // GetGrantsForSubjectAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetGrantsForSubjectAsync_ReturnsOnlyActiveGrants()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        await SeedGrantAsync(db, subjectId: _ownerSubjectId);
        await SeedGrantAsync(db, subjectId: _ownerSubjectId, revokedAt: DateTime.UtcNow);

        var service = CreateService(db);
        var grants = await service.GetGrantsForSubjectAsync(_ownerSubjectId);

        Assert.Single(grants);
    }

    [Fact]
    public async Task GetGrantsForSubjectAsync_ReturnsEmptyWhenNoGrants()
    {
        using var db = CreateDbContext();
        var service = CreateService(db);
        var grants = await service.GetGrantsForSubjectAsync(Guid.CreateVersion7());

        Assert.Empty(grants);
    }

    // ---------------------------------------------------------------
    // GetGrantForSubjectAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetGrantForSubjectAsync_ReturnsTheSubjectsActiveGrant()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, subjectId: _ownerSubjectId);

        var service = CreateService(db);
        var grant = await service.GetGrantForSubjectAsync(grantId, _ownerSubjectId);

        grant.Should().NotBeNull();
        grant!.Id.Should().Be(grantId);
        grant.ClientId.Should().Be(TestClientId, "the caller audits the revocation by client id");
    }

    [Fact]
    public async Task GetGrantForSubjectAsync_ReturnsNullForAnotherSubjectsGrant()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        var otherSubjectId = Guid.CreateVersion7();
        await SeedSubjectAsync(db, otherSubjectId, "Other");
        var grantId = await SeedGrantAsync(db, subjectId: otherSubjectId);

        var service = CreateService(db);

        (await service.GetGrantForSubjectAsync(grantId, _ownerSubjectId)).Should().BeNull();
    }

    [Fact]
    public async Task GetGrantForSubjectAsync_ReturnsNullForARevokedGrant()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(
            db, subjectId: _ownerSubjectId, revokedAt: DateTime.UtcNow);

        var service = CreateService(db);

        (await service.GetGrantForSubjectAsync(grantId, _ownerSubjectId)).Should().BeNull();
    }

    // ---------------------------------------------------------------
    // UpdateGrantAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateGrantAsync_UpdatesLabel()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, label: "Old Label");

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(grantId, _ownerSubjectId, FullAccessBounds, label: "New Label");

        Assert.NotNull(result);
        Assert.Equal("New Label", result.Label);
    }

    [Fact]
    public async Task UpdateLastUsedAsync_RecordsTheInjectedTime()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = CreateService(db, time);

        await service.UpdateLastUsedAsync(grantId, ipAddress: null, userAgent: null);

        var grant = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId);
        Assert.Equal(time.GetUtcNow().UtcDateTime, grant.LastUsedAt);
    }

    [Fact]
    public async Task UpdateGrantAsync_UpdatesScopes()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db,
            scopes: new List<string> { "glucose.read" });

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(grantId, _ownerSubjectId, FullAccessBounds,
            scopes: new[] { "glucose.read", "treatments.readwrite" });

        Assert.NotNull(result);
        Assert.Contains("glucose.read", result.Scopes);
        Assert.Contains("treatments.readwrite", result.Scopes);
    }

    [Fact]
    public async Task UpdateGrantAsync_ToNoScopes_IsRefusedAndLeavesTheGrant()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, scopes: new List<string> { "glucose.read" });

        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateGrantAsync(grantId, _ownerSubjectId, FullAccessBounds, scopes: []));

        var grant = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId);
        Assert.Equal(["glucose.read"], grant.Scopes);
    }

    [Fact]
    public async Task UpdateGrantAsync_EvictsTheCachedGuestSession()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db,
            grantType: OAuthGrantTypes.Guest,
            scopes: [Scope.GlucoseRead, Scope.TreatmentsRead]);

        // The cached session carries the grant's scopes, so narrowing them has to take effect now
        // rather than at the end of the 30-second TTL. Same structural hole as the DELETE path: this
        // method filters on the grant id and the owning subject with no GrantType filter, and a guest
        // grant's SubjectId is the data owner.
        _guestSessionCache.Set(
            _testTenantId,
            grantId,
            new GuestSessionInfo(
                grantId,
                _testTenantId,
                _ownerSubjectId,
                [Scope.GlucoseRead, Scope.TreatmentsRead],
                null,
                DateTime.UtcNow.AddHours(1)));

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(
            grantId, _ownerSubjectId, FullAccessBounds, scopes: [Scope.GlucoseRead]);

        result!.Scopes.Should().Equal([Scope.GlucoseRead]);
        _guestSessionCache.TryGet(_testTenantId, grantId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateGrantAsync_RefusesToWidenAGuestGrant()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db,
            grantType: OAuthGrantTypes.Guest,
            scopes: [Scope.GlucoseRead]);

        var service = CreateService(db);

        // A guest link is a read-only share with no account behind it. The data owner it belongs to
        // reaches it here, so the cap has to hold against the owner too.
        foreach (var widened in new[] { Scope.FullAccess, Scope.GlucoseReadWrite })
        {
            var attempt = () => service.UpdateGrantAsync(
                grantId, _ownerSubjectId, FullAccessBounds, scopes: [widened]);

            await attempt.Should().ThrowAsync<ArgumentException>()
                .Where(e => e.Message.Contains("not allowed for guest links"));
        }

        var entity = await db.OAuthGrants.AsNoTracking().FirstAsync(g => g.Id == grantId);
        entity.Scopes.Should().Equal([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UpdateGrantAsync_RefusesAnUnrecognisedScope()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, scopes: [Scope.GlucoseRead]);

        var service = CreateService(db);

        // OAuthController.UpdateGrant catches ArgumentException as invalid_scope; nothing threw it
        // before, so the API advertised validation it did not perform.
        var attempt = () => service.UpdateGrantAsync(
            grantId, _ownerSubjectId, FullAccessBounds, scopes: ["glucose.everything"]);

        await attempt.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.Message.Contains("not a recognised scope"));

        var entity = await db.OAuthGrants.AsNoTracking().FirstAsync(g => g.Id == grantId);
        entity.Scopes.Should().Equal([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UpdateGrantAsync_AllowsAWriteScopeOnAnAppGrant()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db,
            grantType: OAuthGrantTypes.App,
            scopes: [Scope.GlucoseRead]);

        var service = CreateService(db);
        var result = await service.UpdateGrantAsync(
            grantId, _ownerSubjectId, FullAccessBounds, scopes: [Scope.FullAccess]);

        // The guest cap is scoped to guest grants: an app grant is still whatever the user consented
        // to, including full access.
        result!.Scopes.Should().Equal([Scope.FullAccess]);
    }

    [Fact]
    public async Task UpdateGrantAsync_BoundsAGuestGrantsScopesByWhatTheCallerMayGive()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, grantType: OAuthGrantTypes.Guest, scopes: [Scope.GlucoseRead]);
        var service = CreateService(db);
        var ceiling = new HashSet<string> { Scope.GlucoseRead };

        var widened = () => service.UpdateGrantAsync(grantId, _ownerSubjectId, new GrantScopeBounds(FullAccessCeiling, ceiling), scopes: [Scope.TreatmentsRead]);
        (await widened.Should().ThrowAsync<GrantCeilingViolationException>())
            .Which.Violation.Code.Should().Be(GrantCeilingViolation.ExceedsGranter);
        (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId)).Scopes.Should().Equal([Scope.GlucoseRead]);

        var held = await service.UpdateGrantAsync(grantId, _ownerSubjectId, new GrantScopeBounds(FullAccessCeiling, ceiling), scopes: [Scope.GlucoseRead]);
        held!.Scopes.Should().Equal([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UpdateGrantAsync_RefusesNewScopesOnAGuestGrantFromACallerWhoMayGiveNothing()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, grantType: OAuthGrantTypes.Guest, scopes: [Scope.GlucoseRead]);
        var service = CreateService(db);

        var attempt = () => service.UpdateGrantAsync(grantId, _ownerSubjectId, new GrantScopeBounds(FullAccessCeiling, null), scopes: [Scope.GlucoseRead]);
        (await attempt.Should().ThrowAsync<GrantCeilingViolationException>())
            .Which.Violation.Code.Should().Be(CallerAccessFromTeam.Code);

        var relabelled = await service.UpdateGrantAsync(grantId, _ownerSubjectId, new GrantScopeBounds(FullAccessCeiling, null), label: "Renamed");
        relabelled!.Label.Should().Be("Renamed");
    }

    [Fact]
    public async Task UpdateGrantAsync_DoesNotBoundAnAppGrantByWhatTheCallerMayGive()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, grantType: OAuthGrantTypes.App, scopes: [Scope.GlucoseRead]);
        var service = CreateService(db);

        var result = await service.UpdateGrantAsync(grantId, _ownerSubjectId, new GrantScopeBounds(FullAccessCeiling, null), scopes: [Scope.TreatmentsRead]);

        result!.Scopes.Should().Equal([Scope.TreatmentsRead]);
    }

    [Fact]
    public async Task UpdateGrant_ByAMixedMember_RefusesAGuestLinkScopeOnlyTheTeamGives()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, grantType: OAuthGrantTypes.Guest, scopes: [Scope.GlucoseRead]);
        var controller = CreateController(
            db, [Scope.GlucoseRead, Scope.TreatmentsRead], new GrantCeiling(new HashSet<string> { Scope.GlucoseRead }, null, false));

        var result = await controller.UpdateGrant(grantId, new UpdateGrantRequest { Scopes = [Scope.TreatmentsRead] });

        result.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<OAuthError>().Which.Error.Should().Be("invalid_scope");
        (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId)).Scopes.Should().Equal([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UpdateGrant_ByATeamOnlyMember_RefusesNewGuestLinkScopes()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, grantType: OAuthGrantTypes.Guest, scopes: [Scope.GlucoseRead]);
        var controller = CreateController(db, [Scope.GlucoseRead], ceiling: null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(
            await controller.UpdateGrant(grantId, new UpdateGrantRequest { Scopes = [Scope.GlucoseRead] }));
        (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId)).Scopes.Should().Equal([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UpdateGrant_BoundsAnAppGrantByEverythingTheCallerHolds()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db, grantType: OAuthGrantTypes.App, scopes: [Scope.GlucoseRead]);
        var controller = CreateController(
            db, [Scope.GlucoseRead, Scope.TreatmentsRead], new GrantCeiling(new HashSet<string> { Scope.GlucoseRead }, null, false));

        var wider = await controller.UpdateGrant(grantId, new UpdateGrantRequest { Scopes = [Scope.DevicesRead] });
        wider.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeOfType<OAuthError>().Which.Error.Should().Be("invalid_scope");

        var held = await controller.UpdateGrant(grantId, new UpdateGrantRequest { Scopes = [Scope.TreatmentsRead] });
        held.Result.Should().BeOfType<OkObjectResult>();
        (await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId)).Scopes.Should().Equal([Scope.TreatmentsRead]);
    }

    private OAuthController CreateController(NocturneDbContext db, IReadOnlyList<string> heldScopes, GrantCeiling? ceiling)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _ownerSubjectId,
        };
        httpContext.SetGrantedScopes((IReadOnlySet<string>)heldScopes.ToHashSet());
        httpContext.SetGrantCeiling(ceiling);

        return new OAuthController(
            Mock.Of<IOAuthClientService>(),
            CreateService(db),
            Mock.Of<IOAuthTokenService>(),
            Mock.Of<IOAuthDeviceCodeService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IJwtService>(),
            Mock.Of<IOAuthTokenRevocationCache>(),
            NullLogger<OAuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task UpdateGrantAsync_ReturnsNullForARevokedGrantAndWritesNothing()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(
            db, label: "Old Label", scopes: [Scope.GlucoseRead], revokedAt: DateTime.UtcNow);
        var service = CreateService(db);

        var result = await service.UpdateGrantAsync(
            grantId, _ownerSubjectId, FullAccessBounds, label: "New Label", scopes: [Scope.TreatmentsRead]);

        result.Should().BeNull();
        var stored = await db.OAuthGrants.AsNoTracking().SingleAsync(g => g.Id == grantId);
        stored.Label.Should().Be("Old Label");
        stored.Scopes.Should().Equal([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task UpdateGrant_ForARevokedGrant_AnswersNotFoundAsForAMissingOne()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var revokedId = await SeedGrantAsync(db, revokedAt: DateTime.UtcNow);
        var controller = CreateController(db, [Scope.GlucoseRead], ceiling: null);

        var revoked = await controller.UpdateGrant(revokedId, new UpdateGrantRequest { Label = "Renamed" });
        var missing = await controller.UpdateGrant(Guid.CreateVersion7(), new UpdateGrantRequest { Label = "Renamed" });

        revoked.Result.Should().BeOfType<NotFoundObjectResult>()
            .Which.Value.Should().BeEquivalentTo(
                missing.Result.Should().BeOfType<NotFoundObjectResult>().Which.Value);
    }

    [Fact]
    public async Task UpdateGrantAsync_ReturnsNullForWrongOwner()
    {
        using var db = CreateDbContext();
        await SeedClientAsync(db);
        await SeedSubjectAsync(db, _ownerSubjectId, "Owner");
        var grantId = await SeedGrantAsync(db);

        var service = CreateService(db);
        var wrongOwnerId = Guid.CreateVersion7();
        var result = await service.UpdateGrantAsync(grantId, wrongOwnerId, FullAccessBounds, label: "Hacked");

        Assert.Null(result);
    }
}
