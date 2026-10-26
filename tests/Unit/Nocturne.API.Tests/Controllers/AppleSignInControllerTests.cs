using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Apple's form POST becomes a 303, so the browser follows it with a GET that carries the
/// SameSite=Lax state cookie.
/// </summary>
public class AppleSignInControllerTests
{
    private readonly Mock<IOidcAuthService> _authService = new();
    private readonly AppleSignInController _controller;

    public AppleSignInControllerTests()
    {
        var notifications = new AppleNotificationService(
            Mock.Of<IOidcProviderService>(), Mock.Of<IOidcConfigurationSource>(), Mock.Of<INocturneDbContextFactory>(),
            Mock.Of<IFirstPartyTokenRepository>(), Mock.Of<IAuthAuditService>(),
            NullLogger<AppleNotificationService>.Instance);

        _controller = new AppleSignInController(
            _authService.Object, notifications, NullLogger<AppleSignInController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_WithAStateItIssued_AnswersSeeOtherToTheRelayUrl()
    {
        const string relay = "/api/auth/oidc/callback?code=c&state=s&provider_user=u";
        _authService.Setup(a => a.GetFormPostRelayUrlAsync("s", "c", null, "{}")).ReturnsAsync(relay);

        var result = await _controller.Callback("c", "s", null, "{}");

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status303SeeOther);
        _controller.Response.Headers.Location.ToString().Should().Be(relay);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Callback_WithNoState_RedirectsToTheErrorPage_WithoutReadingIt()
    {
        var result = await _controller.Callback("c", null, null, null);

        result.Should().BeOfType<RedirectResult>().Which.Url.Should().StartWith("/auth/error?error=invalid_state");
        _authService.VerifyNoOtherCalls();
    }
}
