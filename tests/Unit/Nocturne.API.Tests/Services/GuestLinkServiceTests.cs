using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
    private readonly GuestLinkService _service;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _dataOwnerId = Guid.CreateVersion7();
    private readonly Guid _creatorId = Guid.CreateVersion7();

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

        _service = new GuestLinkService(
            _dbContext,
            NullLogger<GuestLinkService>.Instance);
    }

    [Fact]
    public async Task CreateGuestLink_ReturnsCodeAndInfo()
    {
        var result = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Test Link", "https://example.com");

        result.Code.Should().MatchRegex(@"^[A-Z2-9]{3}-[A-Z2-9]{4}$");
        result.FullUrl.Should().StartWith("https://example.com/guest/");
        result.Info.Label.Should().Be("Test Link");
        result.Info.Status.Should().Be(GuestLinkStatus.Pending);
        result.Info.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(48), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateGuestLink_StoresHashNotPlaintext()
    {
        var result = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Hash Test", "https://example.com");

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
            [OAuthScopes.EntriesReadWrite]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not allowed*");
    }

    [Fact]
    public async Task CreateGuestLink_EnforcesMaxActiveLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, $"Link {i}", "https://example.com");
        }

        var act = () => _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Link 6", "https://example.com");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Maximum*5*");
    }

    [Fact]
    public async Task CreateGuestLink_DefaultScopes_AreReadOnly()
    {
        var result = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Defaults", "https://example.com");

        result.Info.Scopes.Should().Contain(OAuthScopes.HealthRead);
        result.Info.Scopes.Should().Contain(OAuthScopes.ProfileRead);
        result.Info.Scopes.Should().Contain(OAuthScopes.ReportsRead);
        result.Info.Scopes.Should().NotContain(s => s.Contains("readwrite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ActivateAsync_ValidCode_ReturnsSession()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Activate Me", "https://example.com");

        var result = await _service.ActivateAsync(created.Code, "1.2.3.4", "TestAgent");

        result.Success.Should().BeTrue();
        result.Session.Should().NotBeNull();
        result.Session!.DataOwnerSubjectId.Should().Be(_dataOwnerId);
        result.Session.Label.Should().Be("Activate Me");
    }

    [Fact]
    public async Task ActivateAsync_AlreadyActivated_Fails()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "One-Time", "https://example.com");
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
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "IP Test", "https://example.com");
        await _service.ActivateAsync(created.Code, "10.0.0.1", "Mozilla/5.0");

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);

        grant.ActivatedAt.Should().NotBeNull();
        grant.ActivatedIp.Should().Be("10.0.0.1");
        grant.ActivatedUserAgent.Should().Be("Mozilla/5.0");
    }

    [Fact]
    public async Task ValidateSessionAsync_ActiveGrant_ReturnsInfo()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Session Test", "https://example.com");
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
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Revoke Test", "https://example.com");
        await _service.ActivateAsync(created.Code, "1.2.3.4", "Agent");
        await _service.RevokeAsync(created.Info.Id, _dataOwnerId);

        var session = await _service.ValidateSessionAsync(created.Info.Id);

        session.Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_SetsRevokedAt()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "To Revoke", "https://example.com");

        var result = await _service.RevokeAsync(created.Info.Id, _dataOwnerId);

        result.Should().BeTrue();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RevokeAsync_DifferentSubject_Fails()
    {
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Not Yours", "https://example.com");
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
        var created = await _service.CreateGuestLinkAsync(_dataOwnerId, _creatorId, "Creator Revoke", "https://example.com");

        var result = await _service.RevokeAsync(created.Info.Id, _creatorId);

        result.Should().BeTrue();

        var grant = await _dbContext.OAuthGrants
            .IgnoreQueryFilters()
            .FirstAsync(g => g.Id == created.Info.Id);
        grant.RevokedAt.Should().NotBeNull();
    }

    public void Dispose() => _dbContext.Dispose();
}
