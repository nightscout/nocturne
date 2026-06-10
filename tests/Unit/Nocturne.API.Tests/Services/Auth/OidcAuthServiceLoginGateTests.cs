using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;
using Subject = Nocturne.Core.Models.Authorization.Subject;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Tests for the cross-tenant login guard in
/// <see cref="OidcAuthService.CompleteLoginAsync"/>. An OIDC identity resolves to a global
/// subject, so a valid external identity must still be a member of the tenant being logged
/// into before a session is issued. Without this gate, any external identity could mint a
/// session on any tenant's subdomain.
/// </summary>
public class OidcAuthServiceLoginGateTests
{
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IOidcProviderService> _providerService = new();
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly Mock<IJwtService> _jwtService = new();
    private readonly Mock<IRefreshTokenService> _refreshTokenService = new();
    private readonly Mock<IHttpClientFactory> _httpFactory = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly Mock<IConfiguration> _configuration = new();
    private readonly OidcAuthService _service;

    public OidcAuthServiceLoginGateTests()
    {
        var options = Options.Create(new OidcOptions());
        _service = new OidcAuthService(
            _providerService.Object,
            _subjectService.Object,
            _sessionService.Object,
            _jwtService.Object,
            _refreshTokenService.Object,
            _httpFactory.Object,
            _tenantMemberService.Object,
            options,
            _configuration.Object,
            NullLogger<OidcAuthService>.Instance);
    }

    private static OidcAuthService.OidcStateData LoginState(string tenantSlug = "erik", string returnUrl = "/")
        => new()
        {
            Intent = "login",
            ReturnUrl = returnUrl,
            ProviderId = Guid.NewGuid(),
            Nonce = "n",
            TenantSlug = tenantSlug,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    private static OidcProvider Provider()
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Google",
            IssuerUrl = "https://accounts.google.com",
            ClientId = "nocturne",
            IsEnabled = true,
        };

    private static OidcAuthService.OidcIdTokenClaims Claims()
        => new() { Sub = "google-123", Email = "user@example.com" };

    private Subject SetupResolvedSubject()
    {
        var subject = new Subject { Id = Guid.NewGuid(), Name = "Rhys", Email = "user@example.com" };
        _subjectService
            .Setup(s => s.FindOrCreateFromOidcAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(subject);
        return subject;
    }

    private void SetupSessionIssuance()
    {
        _subjectService.Setup(s => s.UpdateLastLoginAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        _subjectService.Setup(s => s.GetSubjectPermissionsAsync(It.IsAny<Guid>())).ReturnsAsync(new List<string>());
        _subjectService.Setup(s => s.GetSubjectRolesAsync(It.IsAny<Guid>())).ReturnsAsync(new List<string>());
        _sessionService
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 3600));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenSubjectIsNotMemberOfTenant_DeniesWithoutIssuingSession()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeFalse();
        result.IsAccessDenied.Should().BeTrue("a non-member must be denied a session");
        result.SubjectId.Should().Be(subject.Id);
        result.Tokens.Should().BeNull("no session may be issued for a non-member");

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the cross-tenant login must not mint a session");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenSubjectIsMemberOfTenant_IssuesSession()
    {
        var subject = SetupResolvedSubject();
        var tenantId = Guid.NewGuid();
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subject.Id, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(), Provider(), Claims(), tenantId, ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        result.IsAccessDenied.Should().BeFalse();
        result.Tokens.Should().NotBeNull();

        _sessionService.Verify(
            s => s.IssueSessionAsync(subject.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteLoginAsync_WhenNoTenantResolved_IssuesSession()
    {
        // Single-tenant / unresolved-tenant deployments have no subdomain tenant to gate on.
        var subject = SetupResolvedSubject();
        SetupSessionIssuance();

        var result = await _service.CompleteLoginAsync(
            LoginState(tenantSlug: null!), Provider(), Claims(), currentTenantId: null, ipAddress: null, userAgent: null);

        result.Success.Should().BeTrue();
        result.IsAccessDenied.Should().BeFalse();

        _tenantMemberService.Verify(
            t => t.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "with no resolved tenant there is nothing to check membership against");
        _sessionService.Verify(
            s => s.IssueSessionAsync(subject.Id, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
