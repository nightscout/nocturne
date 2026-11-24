using Nocturne.Infrastructure.Data.Services;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class OidcControllerLoginCallbackTests
{
    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
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
            _auditService.Object,
            new Mock<ITenantMemberService>().Object,
            new RecoverySession(new Mock<INocturneDbContextFactory>().Object, new Mock<ILoginCodeService>().Object, new Mock<ITenantMemberService>().Object, Options.Create(new OidcOptions()), NullLogger<RecoverySession>.Instance),
            Options.Create(_options),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance);

        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        _controller.HttpContext.Request.Headers["Cookie"] = $"{_options.Cookie.StateCookieName}=state";
    }

    private void SetupCallbackResult(OidcCallbackResult result) =>
        _authService
            .Setup(s => s.HandleCallbackAsync(
                "code", "state", "state", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
                It.IsAny<string?>()))
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

    /// <summary>
    /// A provider sign-in over a session redeemed from a login code ends that session, so its refresh
    /// token does not stay live beside the person's own sign-in; over an ordinary one it ends nothing.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Callback_OverAHandedOffSession_EndsIt(bool handedOff)
    {
        var subjectId = Guid.NewGuid();
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(subjectId, "previous-session", handedOff));
        var audit = new Mock<IAuthAuditService>();
        _controller.HttpContext.RequestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(audit.Object).BuildServiceProvider();
        _controller.HttpContext.Request.Headers["Cookie"] =
            $"{_options.Cookie.StateCookieName}=state; {_options.Cookie.RefreshTokenName}=presented";
        SetupCallbackResult(OidcCallbackResult.Succeeded(
            new OidcTokenResponse
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                SubjectId = subjectId,
            },
            new OidcUserInfo { SubjectId = subjectId },
            "/"));

        await _controller.Callback("code", "state", null, null);

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(subjectId, "previous-session", It.IsAny<string>()),
            handedOff ? Times.Once() : Times.Never());
        audit.Verify(
            a => a.LogAsync(AuthAuditEventType.Logout, subjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
                null, It.Is<string?>(d => d != null && d.Contains("replaced_by_sign_in")), null, null, null),
            handedOff ? Times.Once() : Times.Never());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_HandsTheTrustedDeviceCookieToTheSignIn()
    {
        _controller.HttpContext.Request.Headers["Cookie"] =
            $"{_options.Cookie.StateCookieName}=state; {SessionCookieExtensions.TrustedDeviceCookieName}=device-token";
        SetupCallbackResult(OidcCallbackResult.Failed("callback_failed"));

        await _controller.Callback("code", "state", null, null);

        _authService.Verify(s => s.HandleCallbackAsync(
            "code", "state", "state", It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(),
            "device-token"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_ForAnUnseenIdentityThatIsNotAMember_RecordsTheProviderAndADigestOfTheIdentity()
    {
        var providerId = Guid.NewGuid();
        var digest = OidcAuthService.ExternalIdentityDigest("https://idp.example.com", "external-123");
        SetupCallbackResult(OidcCallbackResult.NotAMember(null, providerId, digest, "/"));
        string? details = null;
        _auditService
            .Setup(a => a.LogAsync(
                AuthAuditEventType.FailedAuth, null, false, It.IsAny<string?>(), It.IsAny<string?>(),
                "not_a_member", It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback<string, Guid?, bool, string?, string?, string?, string?, Guid?, AuthAuditActor?, Guid?>(
                (_, _, _, _, _, _, json, _, _, _) => details = json)
            .Returns(Task.CompletedTask);

        var result = await _controller.Callback("code", "state", null, null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be("/auth/login?reason=not_a_member&returnUrl=%2F");
        details.Should().NotBeNull("the refusal is audited with no subject to name");
        using var json = System.Text.Json.JsonDocument.Parse(details!);
        json.RootElement.GetProperty("providerId").GetGuid().Should().Be(providerId);
        json.RootElement.GetProperty("externalIdentityDigest").GetString().Should().Be(digest);
        details.Should().NotContain("external-123");
        SetCookieHeader().Should().NotContain(_options.Cookie.AccessTokenName);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("/reports?days=7", "/auth/login?reason=not_a_member&returnUrl=%2Freports%3Fdays%3D7")]
    [InlineData("/join", "/auth/login?reason=not_a_member&returnUrl=%2Fjoin")]
    [InlineData("/start", "/auth/login?reason=not_a_member&returnUrl=%2Fstart")]
    [InlineData(null, "/auth/login?reason=not_a_member")]
    public async Task Callback_ForARefusedSignIn_SendsTheLoginPageTheReasonAndTheReturnUrl(
        string? returnUrl, string expected)
    {
        SetupCallbackResult(OidcCallbackResult.NotAMember(Guid.NewGuid(), Guid.NewGuid(), null, returnUrl));

        var result = await _controller.Callback("code", "state", null, null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be(expected);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("/start?token=abc")]
    [InlineData("/start?token=abc&slug=erik&name=Erik%27s%20site")]
    [InlineData("/join?token=abc")]
    public async Task Callback_ForARefusedSignInThatStartedFromAnInviteOrCreationLink_ReturnsToThatLink(string link)
    {
        SetupCallbackResult(OidcCallbackResult.NotAMember(null, Guid.NewGuid(), "digest", link));

        var result = await _controller.Callback("code", "state", null, null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().Be(link);
        SetCookieHeader().Should().NotContain(_options.Cookie.AccessTokenName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NotAMemberDetails_ForAKnownSubject_NamesTheProviderAndNothingDerivedFromTheIdentity()
    {
        var providerId = Guid.NewGuid();

        var details = OidcController.NotAMemberDetails(
            OidcCallbackResult.NotAMember(Guid.NewGuid(), providerId, null, "/"));

        using var json = System.Text.Json.JsonDocument.Parse(details);
        json.RootElement.GetProperty("providerId").GetGuid().Should().Be(providerId);
        json.RootElement.TryGetProperty("externalIdentityDigest", out _).Should().BeFalse();
    }
}
