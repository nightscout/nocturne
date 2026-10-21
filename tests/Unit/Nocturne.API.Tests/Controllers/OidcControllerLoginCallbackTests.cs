using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class OidcControllerLoginCallbackTests
{
    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly OidcOptions _options = new()
    {
        Cookie = new CookieSettings
        {
            AccessTokenName = ".Nocturne.AccessToken",
            RefreshTokenName = ".Nocturne.RefreshToken",
            StateCookieName = ".Nocturne.OidcState",
            Secure = true,
            Path = "/app",
        },
    };
    private readonly OidcController _controller;

    public OidcControllerLoginCallbackTests()
    {
        _controller = new OidcController(
            _authService.Object,
            new Mock<IOidcProviderService>().Object,
            new Mock<ISubjectService>().Object,
            new Mock<IAuthAuditService>().Object,
            new Mock<ITenantMemberService>().Object,
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance);

        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        _controller.HttpContext.Request.Headers["Cookie"] = $"{_options.Cookie.StateCookieName}=state";
    }

    private void SetupCallbackResult(OidcCallbackResult result) =>
        _authService
            .Setup(s => s.HandleCallbackAsync(
                "code", "state", "state", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(result);

    private string SetCookieHeader() => _controller.HttpContext.Response.Headers.SetCookie.ToString();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_WhenASecondFactorIsRequired_HandsTheStepUpTokenToTheLoginPageWithoutASession()
    {
        SetupCallbackResult(OidcCallbackResult.SecondFactorRequired(
            Guid.NewGuid(), "step-up-token", "/reports?days=7", Guid.NewGuid()));

        var result = await _controller.Callback("code", "state", null, null);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/auth/login?returnUrl=%2Freports%3Fdays%3D7");

        var setCookie = SetCookieHeader();
        setCookie.Should().MatchRegex(
            $@"{Regex.Escape(SessionCookieExtensions.TotpStepUpCookieName)}=step-up-token;[^,]*path=/(;|$)",
            "the web app deletes it at the root path");
        setCookie.Should().Contain("httponly");
        setCookie.Should().NotContain(_options.Cookie.AccessTokenName);
        setCookie.Should().NotContain(_options.Cookie.RefreshTokenName);
        setCookie.Should().NotContain(SessionCookieExtensions.IsAuthenticatedCookieName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_WhenNoSecondFactorIsRequired_IssuesTheSessionAndReturns()
    {
        var subjectId = Guid.NewGuid();
        SetupCallbackResult(OidcCallbackResult.Succeeded(
            new OidcTokenResponse
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                SubjectId = subjectId,
            },
            new OidcUserInfo { SubjectId = subjectId },
            "/reports"));

        var result = await _controller.Callback("code", "state", null, null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/reports");

        var setCookie = SetCookieHeader();
        setCookie.Should().Contain($"{_options.Cookie.AccessTokenName}=access");
        setCookie.Should().NotContain(SessionCookieExtensions.TotpStepUpCookieName);
    }
}
