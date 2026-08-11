using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Tests for the return-URL validation on the OIDC login endpoint. A return URL is a
/// post-login redirect target supplied by the caller, so it must never leave the site:
/// only site-local paths and absolute URLs on the deployment's public origin
/// (scheme + authority, derived from <c>BASE_DOMAIN</c>) are accepted.
/// </summary>
public class OidcControllerReturnUrlTests
{
    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly OidcController _controller;

    public OidcControllerReturnUrlTests()
    {
        _authService
            .Setup(s => s.GenerateAuthorizationUrlAsync(
                It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new OidcAuthorizationRequest
            {
                AuthorizationUrl = "https://idp.example/auth?x=1",
                State = "state",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                ProviderId = Guid.NewGuid(),
            });

        var options = new OidcOptions
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

        _controller = new OidcController(
            _authService.Object,
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            new Mock<IAuthAuditService>().Object,
            new Mock<ITenantMemberService>().Object,
            Options.Create(options),
            Options.Create(new BaseDomainOptions { BaseDomain = "cgm.example.com" }),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/settings/account")]
    [InlineData("/join?token=abc")]
    [InlineData("https://cgm.example.com")]
    [InlineData("https://cgm.example.com/reports")]
    [InlineData("https://cgm.example.com?welcome=1")]
    [InlineData("HTTPS://CGM.EXAMPLE.COM/reports")]
    public async Task Login_WithSiteLocalOrOwnOriginReturnUrl_Redirects(string returnUrl)
    {
        var result = await _controller.Login(returnUrl: returnUrl);

        result.Should().BeOfType<RedirectResult>();
    }

    [Theory]
    [InlineData("//evil.com")] // scheme-relative: browsers resolve off-site
    [InlineData("/\\evil.com")] // backslash variant some browsers normalize to //
    [InlineData("https://evil.com/")]
    [InlineData("https://cgm.example.com.evil.com/")] // origin as subdomain prefix
    [InlineData("https://cgm.example.com@evil.com/")] // origin as userinfo
    [InlineData("https://user@cgm.example.com/")] // userinfo on the real origin
    [InlineData("http://cgm.example.com/")] // scheme downgrade
    [InlineData("javascript:alert(1)")]
    [InlineData("settings/account")] // path-relative: resolves against the current path
    public async Task Login_WithOffsiteReturnUrl_ReturnsBadRequest(string returnUrl)
    {
        var result = await _controller.Login(returnUrl: returnUrl);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Login_WithAbsoluteReturnUrl_WhenNoBaseDomainConfigured_ReturnsBadRequest()
    {
        var controller = new OidcController(
            _authService.Object,
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            new Mock<IAuthAuditService>().Object,
            new Mock<ITenantMemberService>().Object,
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions()),
            NullLogger<OidcController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        var result = await controller.Login(returnUrl: "https://cgm.example.com/reports");

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
