using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Tests.Services;

public class GuestLinkServiceTests : IDisposable
{
    private readonly NocturneDbContext _dbContext;
    private readonly GuestSessionCacheService _sessionCache;
    private readonly GuestLinkService _service;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _dataOwnerId = Guid.CreateVersion7();
    private readonly Guid _creatorId = Guid.CreateVersion7();

    private static IReadOnlySet<string> Unbounded() =>
        new HashSet<string>(StringComparer.Ordinal) { Scope.FullAccess };

    public GuestLinkServiceTests()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new NocturneDbContext(options);
        _dbContext.TenantId = _tenantId;

        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });
        _dbContext.SaveChanges();

        _sessionCache = new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()));

        _service = new GuestLinkService(
            _dbContext,
            _sessionCache,
            new GrantRevocationService(_sessionCache),
            NullLogger<GuestLinkService>.Instance);
    }

    [Fact]
    public async Task CreateGuestLink_ReturnsCodeAndInfo()
    {
        var result = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Test Link", "https://example.com", Unbounded());

        result.Code.Should().MatchRegex(@"^[A-Z2-9]{3}-[A-Z2-9]{4}$");
        result.FullUrl.Should().StartWith("https://example.com/guest/");
        result.Info.Label.Should().Be("Test Link");
        result.Info.Status.Should().Be(GuestLinkStatus.Pending);
        result.Info.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(48), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateGuestLink_StoresHashNotPlaintext()
    {
        var result = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Hash Test", "https://example.com", Unbounded());

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == result.Info.Id);

        var plainCode = result.Code.Replace("-", "");
        grant.TokenHash.Should().NotBe(plainCode);

        var expectedHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(plainCode.ToUpperInvariant()))
        ).ToLowerInvariant();
        grant.TokenHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task CreateGuestLink_RejectsWriteScopes()
    {
        var act = () => _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Bad Scopes", "https://example.com",
            Unbounded(), [Scope.GlucoseReadWrite]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not allowed*");
    }

    [Theory]
    [InlineData(Scope.MembersManage)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.RolesManage)]
    [InlineData(Scope.TenantSettings)]
    [InlineData(Scope.SharingManage)]
    [InlineData(Scope.SharingGuest)]
    [InlineData(Scope.AuditRead)]
    [InlineData(Scope.AuditManage)]
    public async Task CreateGuestLink_RejectsTenantAdministrationScopes(string scope)
    {
        var act = () => _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Admin Scopes", "https://example.com", Unbounded(), [scope]);

        // An administration atom is absent from ValidRequestScopes, so the recognised-scope guard
        // rejects it before the guest cap is consulted. Either guard is a correct rejection, so the
        // assertion accepts both rather than pinning one wording.
        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.Message.Contains("not a recognised scope")
                        || e.Message.Contains("not allowed for guest links"));
    }

    [Fact]
    public async Task CreateGuestLink_EnforcesMaxActiveLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, $"Link {i}", "https://example.com", Unbounded());
        }

        var act = () => _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Link 6", "https://example.com", Unbounded());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Maximum*5*");
    }

    [Fact]
    public async Task CreateGuestLink_DefaultScopes_AreReadOnly()
    {
        var result = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Defaults", "https://example.com", Unbounded());

        result.Info.Scopes.Should().BeEquivalentTo(
            Scope.HealthReadExpansion
                .Where(s => Scope.AllowedGuestScopes.Contains(s))
                .Append(Scope.TherapyRead)
                .Append(Scope.ReportsRead)
                .Distinct());
        result.Info.Scopes.Should().NotContain(Scope.HealthRead);
        result.Info.Scopes.Should().NotContain(Scope.FoodRead);
        result.Info.Scopes.Should().NotContain(s => s.Contains("readwrite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateGuestLink_ExplicitScopeWiderThanCreator_IsRefusedAndWritesNoGrant()
    {
        var creator = new HashSet<string> { Scope.GlucoseRead, Scope.SharingGuest };

        var act = () => _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Too Wide", "https://example.com",
            scopes: [Scope.TreatmentsRead], creatorScopes: creator);

        var thrown = await act.Should().ThrowAsync<GrantCeilingViolationException>();
        thrown.Which.Violation.Code.Should().Be(GrantCeilingViolation.ExceedsGranter);

        (await _dbContext.OAuthGrants.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CreateGuestLink_NoScopes_NarrowsDefaultsToCreator()
    {
        var creator = new HashSet<string> { Scope.GlucoseRead, Scope.SharingGuest };

        var result = await _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Narrowed", "https://example.com", creatorScopes: creator);

        result.Info.Scopes.Should().BeEquivalentTo([Scope.GlucoseRead]);
        result.Info.Scopes.Should().NotContain(Scope.HealthRead);
        result.Info.Scopes.Should().NotContain(Scope.TherapyRead);
    }

    [Fact]
    public async Task CreateGuestLink_NoScopes_NothingSurvives_IsRefused()
    {
        var creator = new HashSet<string> { Scope.SharingGuest };

        var act = () => _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Empty", "https://example.com", creatorScopes: creator);

        var thrown = await act.Should().ThrowAsync<GrantCeilingViolationException>();
        thrown.Which.Violation.Code.Should().Be(GrantCeilingViolation.ExceedsGranter);
    }

    [Fact]
    public async Task CreateGuestLink_HealthReadAlias_AllowedWhenCreatorHoldsEveryAtom()
    {
        var creator = new HashSet<string>(Scope.HealthReadExpansion, StringComparer.Ordinal);

        var result = await _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Alias", "https://example.com",
            scopes: [Scope.HealthRead], creatorScopes: creator);

        result.Info.Scopes.Should().Contain(Scope.HealthRead);
    }

    [Fact]
    public async Task ActivateAsync_ValidCode_ReturnsSession()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Activate Me", "https://example.com", Unbounded());

        var result = await _service.ActivateAsync(created.Code, "1.2.3.4", "TestAgent");

        result.Success.Should().BeTrue();
        result.Session.Should().NotBeNull();
        result.Session!.DataOwnerSubjectId.Should().Be(_dataOwnerId);
        result.Session.Label.Should().Be("Activate Me");
    }

    [Fact]
    public async Task ActivateAsync_AlreadyActivated_Fails()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "One-Time", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent1");

        var result = await _service.ActivateAsync(created.Code, "5.6.7.8", "Agent2");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired code");
    }

    [Fact]
    public async Task ActivateAsync_InvalidCode_Fails()
    {
        var result = await _service.ActivateAsync("XXX-YYYY", null, null);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired code");
    }

    [Fact]
    public async Task ActivateAsync_RecordsIpAndUserAgent()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "IP Test", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "10.0.0.1", "Mozilla/5.0");

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);

        grant.ActivatedAt.Should().NotBeNull();
        grant.ActivatedIp.Should().Be("10.0.0.1");
        grant.ActivatedUserAgent.Should().Be("Mozilla/5.0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateGuestLink_HistoryLimit_IsStoredAndCarriedIntoTheSession(bool limitTo24Hours)
    {
        var created = await _service.CreateGuestLinkAsync(
            _dataOwnerId, _creatorId, "Clamp Test", "https://example.com", Unbounded(), limitTo24Hours: limitTo24Hours);

        var grant = await _dbContext.OAuthGrants.IgnoreQueryFilters().FirstAsync(g => g.Id == created.Info.Id);
        grant.LimitTo24Hours.Should().Be(limitTo24Hours);

        var activation = await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");
        activation.Session!.LimitTo24Hours.Should().Be(limitTo24Hours);
        (await _service.ValidateSessionAsync(created.Info.Id))!.LimitTo24Hours.Should().Be(limitTo24Hours);
    }

    [Fact]
    public async Task ValidateSessionAsync_ActiveGrant_ReturnsInfo()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Session Test", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");

        var session = await _service.ValidateSessionAsync(created.Info.Id);

        session.Should().NotBeNull();
        session!.GrantId.Should().Be(created.Info.Id);
        session.DataOwnerSubjectId.Should().Be(_dataOwnerId);
        session.Label.Should().Be("Session Test");
    }

    [Fact]
    public async Task ValidateSessionAsync_RevokedGrant_ReturnsNull()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Revoke Test", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);

        var session = await _service.ValidateSessionAsync(created.Info.Id);

        session.Should().BeNull();
    }

    [Fact]
    public async Task ValidateSessionAsync_CarriesGrantTenant()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Tenant Test", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");

        var session = await _service.ValidateSessionAsync(created.Info.Id);

        session.Should().NotBeNull();
        session!.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task ActivateAsync_SessionCarriesGrantTenant()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Tenant Activate", "https://example.com", Unbounded());

        var result = await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");

        result.Session!.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task DismissAsync_EvictsCachedSession()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Evict On Dismiss", "https://example.com", Unbounded());
        var activation = await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);
        _sessionCache.Set(_tenantId, created.Info.Id, activation.Session);

        await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        _sessionCache.TryGet(_tenantId, created.Info.Id, out _).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeAsync_DifferentSubject_Fails()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Not Yours", "https://example.com", Unbounded());
        var strangerId = Guid.CreateVersion7();

        var result = await _service.RevokeAsync(created.Info.Id, strangerId);

        result.Should().BeFalse();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_Creator_CanRevoke()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Creator Revoke", "https://example.com", Unbounded());

        var result = await _service.RevokeAsync(created.Info.Id, _creatorId);

        result.Should().BeTrue();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DismissAsync_RevokedLink_SetsDismissedAt()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Revoked Dismiss", "https://example.com", Unbounded());
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);

        var result = await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        result.Should().BeTrue();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.DismissedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DismissAsync_ExpiredLink_SetsDismissedAt()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Expired Dismiss", "https://example.com", Unbounded());

        // Force expiration by backdating ExpiresAt
        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.ExpiresAt = DateTime.UtcNow.AddHours(-1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        result.Should().BeTrue();

        await _dbContext.Entry(grant).ReloadAsync();
        grant.DismissedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DismissAsync_ActiveLink_ReturnsFalse()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Active No Dismiss", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");

        var result = await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        result.Should().BeFalse();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.DismissedAt.Should().BeNull();
    }

    [Fact]
    public async Task DismissAsync_PendingLink_ReturnsFalse()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Pending No Dismiss", "https://example.com", Unbounded());

        var result = await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        result.Should().BeFalse();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.DismissedAt.Should().BeNull();
    }

    [Fact]
    public async Task DismissAsync_WrongOwner_ReturnsFalse()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Wrong Owner Dismiss", "https://example.com", Unbounded());
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);
        var strangerId = Guid.CreateVersion7();

        var result = await _service.DismissAsync(created.Info.Id, strangerId);

        result.Should().BeFalse();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.DismissedAt.Should().BeNull();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_NoLinks_ReturnsFalse()
    {
        (await _service.HasRedeemableCodeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_UnusedLink_ReturnsTrue()
    {
        await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Unused", "https://example.com", Unbounded());

        (await _service.HasRedeemableCodeAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_ActivatedLink_ReturnsFalse()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Used", "https://example.com", Unbounded());
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");

        (await _service.HasRedeemableCodeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_RevokedLink_ReturnsFalse()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Revoked", "https://example.com", Unbounded());
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);

        (await _service.HasRedeemableCodeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_ExpiredLink_ReturnsFalse()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Expired", "https://example.com", Unbounded());
        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.ExpiresAt = DateTime.UtcNow.AddHours(-1);
        await _dbContext.SaveChangesAsync();

        (await _service.HasRedeemableCodeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_NonGuestGrant_ReturnsFalse()
    {
        _dbContext.OAuthGrants.Add(new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _dataOwnerId,
            GrantType = OAuthGrantTypes.Direct,
            Scopes = [Scope.HealthRead],
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await _dbContext.SaveChangesAsync();

        (await _service.HasRedeemableCodeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task HasRedeemableCodeAsync_OtherTenantsLink_ReturnsFalse()
    {
        await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Tenant A", "https://example.com", Unbounded());

        _dbContext.TenantId = Guid.CreateVersion7();

        (await _service.HasRedeemableCodeAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task GetGuestLinksAsync_ExcludesDismissedByDefault()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Dismissed Link", "https://example.com", Unbounded());
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);
        await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        var undismissed = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Visible Link", "https://example.com", Unbounded());

        var links = await _service.GetGuestLinksAsync(_dataOwnerId);

        links.Should().ContainSingle(l => l.Id == undismissed.Info.Id);
        links.Should().NotContain(l => l.Id == created.Info.Id);
    }

    [Fact]
    public async Task GetGuestLinksAsync_IncludesDismissedWhenRequested()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Dismissed Link 2", "https://example.com", Unbounded());
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);
        await _service.DismissAsync(created.Info.Id, _dataOwnerId);

        var undismissed = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Visible Link 2", "https://example.com", Unbounded());

        var links = await _service.GetGuestLinksAsync(_dataOwnerId, includeDismissed: true);

        links.Should().Contain(l => l.Id == created.Info.Id);
        links.Should().Contain(l => l.Id == undismissed.Info.Id);
    }

    public void Dispose() => _dbContext.Dispose();
}
