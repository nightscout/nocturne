using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Sign in with Apple server-to-server notifications: only one Apple signed for this Services ID
/// is acted on, and each event changes the identity and sessions it names.
/// </summary>
public class AppleNotificationServiceTests : IDisposable
{
    private const string Issuer = "https://appleid.apple.com";
    private const string ServicesId = "run.nocturne.signin";
    private const string AppleSub = "001234.apple-user";

    private readonly NocturneDbContext _db = TestDbContextFactory.CreateInMemoryContext();
    private readonly RsaSecurityKey _appleKey = new(RSA.Create(2048)) { KeyId = "apple-key" };
    private readonly Mock<IFirstPartyTokenRepository> _sessions = new();
    private readonly Mock<IOidcProviderService> _providers = new();
    private readonly Mock<IOidcConfigurationSource> _configurations = new();
    private readonly AppleNotificationService _service;
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _providerId = Guid.NewGuid();

    public AppleNotificationServiceTests()
    {
        _providers.Setup(p => p.GetEnabledProvidersAsync()).ReturnsAsync(
        [
            new OidcProvider { Id = Guid.NewGuid(), ProviderType = OidcProviderType.Oidc, IssuerUrl = "https://other", ClientId = "x" },
            new OidcProvider
            {
                Id = _providerId,
                ProviderType = OidcProviderType.Apple,
                IssuerUrl = Issuer,
                ClientId = ServicesId,
                Apple = new AppleProviderSettings { PrimaryAppId = "run.nocturne.app" },
            },
        ]);

        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        configuration.SigningKeys.Add(_appleKey);
        _configurations.Setup(c => c.GetAsync(Issuer, It.IsAny<CancellationToken>())).ReturnsAsync(configuration);

        _service = new AppleNotificationService(
            _providers.Object, _configurations.Object, _db.AsFactory(), _sessions.Object,
            Mock.Of<IAuthAuditService>(), NullLogger<AppleNotificationService>.Instance);

        _db.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "ada", IsActive = true });
        _db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            ProviderId = _providerId,
            OidcSubjectId = AppleSub,
            Issuer = Issuer,
            Email = "old@privaterelay.appleid.com",
            ProviderRefreshTokenEncrypted = [1, 2, 3],
            LinkedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private string Notification(
        string type, string? email = null, string audience = ServicesId, SecurityKey? key = null,
        bool eventsAsString = true, string sub = AppleSub, DateTime? expires = null)
    {
        var events = new Dictionary<string, object> { ["type"] = type, ["sub"] = sub, ["event_time"] = 1700000000000 };
        if (email is not null)
            events["email"] = email;

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            IssuedAt = expires is null ? DateTime.UtcNow : expires.Value.AddMinutes(-5),
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["jti"] = Guid.NewGuid().ToString(),
                ["events"] = eventsAsString ? JsonSerializer.Serialize(events) : events,
            },
            SigningCredentials = new SigningCredentials(key ?? _appleKey, SecurityAlgorithms.RsaSha256),
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConsentRevoked_DropsTheTokenAndEndsSessions_ButKeepsTheIdentity()
    {
        (await _service.HandleAsync(Notification("consent-revoked"))).Should().BeTrue();

        var identity = _db.SubjectOidcIdentities.Single();
        await _db.Entry(identity).ReloadAsync();
        identity.ProviderRefreshTokenEncrypted.Should().BeNull();
        _sessions.Verify(s => s.RevokeAllForSubjectAsync(_subjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AccountDeleted_RemovesTheIdentityAndEndsSessions()
    {
        (await _service.HandleAsync(Notification("account-deleted"))).Should().BeTrue();

        _db.ChangeTracker.Clear();
        _db.SubjectOidcIdentities.Should().BeEmpty();
        _sessions.Verify(s => s.RevokeAllForSubjectAsync(_subjectId, It.IsAny<string>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EmailEnabled_UpdatesTheIdentityEmail()
    {
        (await _service.HandleAsync(Notification("email-enabled", email: "new@privaterelay.appleid.com"))).Should().BeTrue();

        _db.ChangeTracker.Clear();
        _db.SubjectOidcIdentities.Single().Email.Should().Be("new@privaterelay.appleid.com");
        _sessions.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task EventsSentAsAnObject_AreReadToo()
    {
        (await _service.HandleAsync(Notification("account-deleted", eventsAsString: false))).Should().BeTrue();

        _db.ChangeTracker.Clear();
        _db.SubjectOidcIdentities.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ThePrimaryAppIdIsAnAcceptedAudience()
    {
        (await _service.HandleAsync(Notification("consent-revoked", audience: "run.nocturne.app"))).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnUnknownUser_IsAcceptedAndChangesNothing()
    {
        (await _service.HandleAsync(Notification("account-deleted", sub: "someone-else"))).Should().BeTrue();

        _db.SubjectOidcIdentities.Should().HaveCount(1);
        _sessions.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANotificationSignedWithAnotherKey_IsRefused()
    {
        var forged = Notification("account-deleted", key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "apple-key" });

        (await _service.HandleAsync(forged)).Should().BeFalse();

        _db.SubjectOidcIdentities.Should().HaveCount(1);
        _sessions.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANotificationForAnotherApp_IsRefused()
    {
        (await _service.HandleAsync(Notification("account-deleted", audience: "com.example.other"))).Should().BeFalse();

        _db.SubjectOidcIdentities.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WithNoAppleProvider_EveryNotificationIsRefused()
    {
        _providers.Setup(p => p.GetEnabledProvidersAsync()).ReturnsAsync([]);

        (await _service.HandleAsync(Notification("account-deleted"))).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnExpiredNotification_IsRefused()
    {
        (await _service.HandleAsync(Notification("account-deleted", expires: DateTime.UtcNow.AddHours(-1))))
            .Should().BeFalse();

        _db.SubjectOidcIdentities.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheOlderAccountDeleteName_IsActedOn()
    {
        (await _service.HandleAsync(Notification("account-delete"))).Should().BeTrue();

        _db.ChangeTracker.Clear();
        _db.SubjectOidcIdentities.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ANewAppleKey_RefreshesTheKeysAndValidates()
    {
        var rotated = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "rotated" };
        var stale = new OpenIdConnectConfiguration { Issuer = Issuer };
        stale.SigningKeys.Add(_appleKey);
        var fresh = new OpenIdConnectConfiguration { Issuer = Issuer };
        fresh.SigningKeys.Add(rotated);
        _configurations.SetupSequence(c => c.GetAsync(Issuer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stale)
            .ReturnsAsync(fresh);

        (await _service.HandleAsync(Notification("consent-revoked", key: rotated))).Should().BeTrue();

        _configurations.Verify(c => c.RequestRefresh(Issuer));
    }
}
