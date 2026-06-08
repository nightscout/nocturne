using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class OidcControllerLinkTests
{
    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly Mock<IOidcProviderService> _providerService = new();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly OidcOptions _options;
    private readonly OidcController _controller;

    public OidcControllerLinkTests()
    {
        _options = new OidcOptions
        {
            Cookie = new CookieSettings
            {
                LinkStateCookieName = ".Nocturne.OidcLinkState",
                AccessTokenName = ".Nocturne.AccessToken",
                RefreshTokenName = ".Nocturne.RefreshToken",
                StateCookieName = ".Nocturne.OidcState",
                Secure = true,
                Path = "/",
            },
        };
        var configuration = new ConfigurationBuilder().Build();

        _controller = new OidcController(
            _authService.Object,
            _providerService.Object,
            _subjectService.Object,
            _auditService.Object,
            _tenantMemberService.Object,
            Options.Create(_options),
            configuration,
            NullLogger<OidcController>.Instance);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
    }

    private void SetAuthenticated(Guid subjectId)
    {
        _controller.HttpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
        };
    }

    private void SetRequestCookie(string name, string value)
    {
        _controller.HttpContext.Request.Headers["Cookie"] = $"{name}={value}";
    }

    // -- Link --------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Link_WhenUnauthenticated_Returns401()
    {
        var result = await _controller.Link(Guid.NewGuid(), null);
        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Link_WhenAuthenticated_CallsServiceAndRedirects()
    {
        var subjectId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        SetAuthenticated(subjectId);

        _authService
            .Setup(s => s.GenerateLinkAuthorizationUrlAsync(providerId, subjectId, "/settings/account"))
            .ReturnsAsync(new OidcAuthorizationRequest
            {
                AuthorizationUrl = "https://idp.example/auth?x=1",
                State = "xyz-state",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                ProviderId = providerId,
            });

        var result = await _controller.Link(providerId, "/settings/account");

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Be("https://idp.example/auth?x=1");

        var setCookie = _controller.HttpContext.Response.Headers["Set-Cookie"].ToString();
        setCookie.Should().Contain(_options.Cookie.LinkStateCookieName);
        setCookie.Should().Contain("xyz-state");
    }

    // -- LinkCallback ------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LinkCallback_WhenUnauthenticated_RedirectsToLogin()
    {
        var result = await _controller.LinkCallback("code", "state", null, null);
        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Contain("/auth/login");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LinkCallback_MissingStateCookie_RedirectsToError()
    {
        SetAuthenticated(Guid.NewGuid());

        var result = await _controller.LinkCallback("code", "state", null, null);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Contain("/auth/error");
        redirect.Url.Should().Contain("invalid_state");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LinkCallback_ServiceReturnsSuccess_RedirectsWithLinkedSuccess()
    {
        var subjectId = Guid.NewGuid();
        SetAuthenticated(subjectId);
        SetRequestCookie(_options.Cookie.LinkStateCookieName, "expected-state");

        _authService
            .Setup(s => s.HandleLinkCallbackAsync(
                "code", "expected-state", "expected-state", subjectId,
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(OidcLinkResult.Succeeded(Guid.NewGuid(), "/settings/account"));

        var result = await _controller.LinkCallback("code", "expected-state", null, null);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Contain("/settings/account");
        redirect.Url.Should().Contain("linked=success");

        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.OidcIdentityLinked,
            subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<Guid?>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LinkCallback_ServiceReturnsIdentityAlreadyLinked_RedirectsToError()
    {
        var subjectId = Guid.NewGuid();
        SetAuthenticated(subjectId);
        SetRequestCookie(_options.Cookie.LinkStateCookieName, "expected-state");

        _authService
            .Setup(s => s.HandleLinkCallbackAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), subjectId,
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(OidcLinkResult.Failed("identity_already_linked", "already linked"));

        var result = await _controller.LinkCallback("code", "state", null, null);

        var redirect = result.Should().BeOfType<RedirectResult>().Subject;
        redirect.Url.Should().Contain("/auth/error");
        redirect.Url.Should().Contain("identity_already_linked");
    }

    // -- GetLinkedIdentities -----------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetLinkedIdentities_WhenUnauthenticated_Returns401()
    {
        var result = await _controller.GetLinkedIdentities();
        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetLinkedIdentities_WhenAuthenticated_ReturnsMappedDto()
    {
        var subjectId = Guid.NewGuid();
        SetAuthenticated(subjectId);

        var linked = new List<SubjectOidcIdentity>
        {
            new()
            {
                Id = Guid.NewGuid(), SubjectId = subjectId, ProviderId = Guid.NewGuid(),
                ProviderName = "Keycloak", Email = "a@x", LinkedAt = DateTime.UtcNow,
            },
            new()
            {
                Id = Guid.NewGuid(), SubjectId = subjectId, ProviderId = Guid.NewGuid(),
                ProviderName = "Google", Email = "b@x", LinkedAt = DateTime.UtcNow,
            },
        };
        _subjectService.Setup(s => s.GetLinkedOidcIdentitiesAsync(subjectId)).ReturnsAsync(linked);

        var result = await _controller.GetLinkedIdentities();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<LinkedOidcIdentitiesResponse>().Subject;
        payload.Identities.Should().HaveCount(2);
        payload.Identities.Select(i => i.ProviderName).Should().BeEquivalentTo(new[] { "Keycloak", "Google" });
    }

    // -- UnlinkIdentity ----------------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnlinkIdentity_WhenLastPrimaryFactor_Returns409LastFactor()
    {
        var subjectId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        SetAuthenticated(subjectId);
        _subjectService.Setup(s => s.TryRemoveOidcIdentityAsync(subjectId, identityId))
            .ReturnsAsync(FactorRemovalResult.LastPrimaryFactor);

        var result = await _controller.UnlinkIdentity(identityId);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnlinkIdentity_WhenNotOwned_Returns404()
    {
        var subjectId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        SetAuthenticated(subjectId);
        _subjectService.Setup(s => s.TryRemoveOidcIdentityAsync(subjectId, identityId))
            .ReturnsAsync(FactorRemovalResult.NotFound);

        var result = await _controller.UnlinkIdentity(identityId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnlinkIdentity_HappyPath_Returns204()
    {
        var subjectId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        SetAuthenticated(subjectId);
        _subjectService.Setup(s => s.TryRemoveOidcIdentityAsync(subjectId, identityId))
            .ReturnsAsync(FactorRemovalResult.Removed);

        var result = await _controller.UnlinkIdentity(identityId);

        result.Should().BeOfType<NoContentResult>();
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.OidcIdentityUnlinked,
            subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<Guid?>()),
            Times.Once);
    }
}
