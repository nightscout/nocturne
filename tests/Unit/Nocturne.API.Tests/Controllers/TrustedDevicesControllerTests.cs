using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Controllers.V4.Identity;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// The account's own list of trusted devices: what it shows, whose devices it can remove, and
/// when removing one takes the cookie off the browser that asked.
/// </summary>
[Trait("Category", "Unit")]
public class TrustedDevicesControllerTests
{
    private const string CurrentToken = "this-browser";

    private readonly Mock<ITrustedDeviceService> _trustedDevices = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _currentDevice = Guid.CreateVersion7();
    private readonly Guid _otherDevice = Guid.CreateVersion7();

    private DefaultHttpContext SignedInContext(bool withCookie = true)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true, SubjectId = _subjectId };
        if (withCookie)
            httpContext.Request.Headers.Cookie = $"{SessionCookieExtensions.TrustedDeviceCookieName}={CurrentToken}";
        return httpContext;
    }

    private TrustedDevicesController CreateController(DefaultHttpContext? httpContext = null)
    {
        var now = DateTime.UtcNow;
        _trustedDevices
            .Setup(t => t.ListAsync(_subjectId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string? token, CancellationToken _) =>
            [
                new TrustedDeviceInfo(_currentDevice, "Firefox on Linux", now, null, now.AddDays(30), IsCurrent: token == CurrentToken),
                new TrustedDeviceInfo(_otherDevice, "Safari on iPhone", now, null, now.AddDays(30), IsCurrent: false),
            ]);

        return new TrustedDevicesController(
            _trustedDevices.Object, _auditService.Object, Options.Create(new OidcOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext ?? SignedInContext() },
        };
    }

    private static string SetCookie(ControllerBase controller) =>
        controller.HttpContext.Response.Headers.SetCookie.ToString();

    private void VerifyRevocationAudited(Times times) =>
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.TrustedDeviceRevoked, _subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), times);

    [Fact]
    public async Task List_MarksTheDeviceTheRequestCameFrom()
    {
        var result = await CreateController().List(CancellationToken.None);

        var devices = result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<List<TrustedDeviceDto>>().Subject;
        devices.Should().HaveCount(2);
        devices.Single(d => d.IsCurrent).Id.Should().Be(_currentDevice);
    }

    [Fact]
    public async Task List_WithoutASubject_IsUnauthorized()
    {
        var result = await CreateController(new DefaultHttpContext()).List(CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Revoke_ADeviceTheCallerDoesNotHold_IsNotFoundAndChangesNothing()
    {
        var someoneElses = Guid.CreateVersion7();
        _trustedDevices
            .Setup(t => t.RevokeAsync(someoneElses, _subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var controller = CreateController();

        var result = await controller.Revoke(someoneElses, CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
        SetCookie(controller).Should().BeEmpty();
        VerifyRevocationAudited(Times.Never());
    }

    [Fact]
    public async Task Revoke_AnotherDevice_LeavesThisBrowsersCookie()
    {
        _trustedDevices
            .Setup(t => t.RevokeAsync(_otherDevice, _subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = CreateController();

        var result = await controller.Revoke(_otherDevice, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        SetCookie(controller).Should().BeEmpty("this browser is still trusted");
        VerifyRevocationAudited(Times.Once());
    }

    [Fact]
    public async Task Revoke_ThisDevice_ExpiresItsCookie()
    {
        _trustedDevices
            .Setup(t => t.RevokeAsync(_currentDevice, _subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var controller = CreateController();

        await controller.Revoke(_currentDevice, CancellationToken.None);

        SetCookie(controller).Should()
            .StartWith($"{SessionCookieExtensions.TrustedDeviceCookieName}=;")
            .And.Contain("expires=Thu, 01 Jan 1970");
    }

    [Fact]
    public async Task RevokeAll_RemovesEveryDeviceAndExpiresTheCookie()
    {
        _trustedDevices
            .Setup(t => t.RevokeAllAsync(_subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        var controller = CreateController();

        var result = await controller.RevokeAll(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<RevokeTrustedDevicesResultDto>()
            .Which.RevokedCount.Should().Be(2);
        SetCookie(controller).Should().StartWith($"{SessionCookieExtensions.TrustedDeviceCookieName}=;");
        VerifyRevocationAudited(Times.Once());
    }

    [Theory]
    [InlineData(nameof(TrustedDevicesController.List))]
    [InlineData(nameof(TrustedDevicesController.Revoke))]
    [InlineData(nameof(TrustedDevicesController.RevokeAll))]
    public void EveryAction_RefusesTheDemoSubjectAndADelegatedCredential(string action)
    {
        var method = typeof(TrustedDevicesController).GetMethod(action)!;

        method.GetCustomAttributes(typeof(DenyDemoSubjectAttribute), inherit: false).Should().NotBeEmpty();
        typeof(TrustedDevicesController)
            .GetCustomAttributes(typeof(RequireInteractiveSessionAttribute), inherit: false)
            .Should().NotBeEmpty("a credential an app holds must not read or change the account's sign-in state");
    }

    /// <summary>
    /// Signing every other device out is what someone does after losing one, so it has to end that
    /// device's trust as well as its session.
    /// </summary>
    [Fact]
    public async Task SigningOutEverywhereElse_EndsTheTrustOfEveryOtherDevice()
    {
        var refreshTokens = new Mock<IRefreshTokenService>();
        var options = new OidcOptions();
        refreshTokens
            .Setup(r => r.FindOwnerAsync("refresh"))
            .ReturnsAsync(new RefreshTokenSession(_subjectId, "session-1"));
        _trustedDevices
            .Setup(t => t.RevokeOthersAsync(_subjectId, CurrentToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var httpContext = SignedInContext();
        httpContext.Request.Headers.Cookie =
            $"{options.Cookie.RefreshTokenName}=refresh; {SessionCookieExtensions.TrustedDeviceCookieName}={CurrentToken}";
        var controller = new SessionsController(
            refreshTokens.Object, _trustedDevices.Object, _auditService.Object,
            Options.Create(options), NullLogger<SessionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.RevokeOthers(CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        _trustedDevices.Verify(
            t => t.RevokeOthersAsync(_subjectId, CurrentToken, It.IsAny<CancellationToken>()), Times.Once);
        VerifyRevocationAudited(Times.Once());
    }
}
