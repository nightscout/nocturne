using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Tests for the guards on TOTP sign-in: it is a second factor reached only with a step-up token
/// from a completed primary factor, and the token is redeemable only on the host it was minted for,
/// under the membership decision the primary factor made there.
/// </summary>
public class TotpControllerLoginGateTests
{
    private readonly Mock<ITotpService> _totpService = new();
    private readonly Mock<ITrustedDeviceService> _trustedDevices = new();
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();

    private const string Apex = "nocturne.example.com";
    private const string TenantHost = "erik.nocturne.example.com";
    private const string DashboardHost = "app.nocturne.example.com";
    private string _host = TenantHost;
    private IServiceProvider? _requestServices;

    private TotpController CreateController()
        => new(
            _totpService.Object,
            _trustedDevices.Object,
            new SignInCompletion(
                _totpService.Object, _tenantMemberService.Object, _sessionService.Object,
                _subjectService.Object, _auditService.Object),
            _subjectService.Object,
            _auditService.Object,
            _tenantAccessor.Object,
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = Apex }),
            NullLogger<TotpController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request = { Host = new HostString(_host) },
                    RequestServices = _requestServices!,
                },
            }
        };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WithoutAValidStepUpToken_DeniesWithoutIssuingSession()
    {
        // No primary factor completed, so no step-up token was ever minted for this code.
        _totpService
            .Setup(s => s.VerifyStepUpAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((TotpLoginResult?)null);

        var controller = CreateController();
        var result = await controller.Login(new TotpLoginRequest
        {
            StepUpToken = "not-a-real-token",
            Code = "123456",
        });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400, "a code alone is not a sign-in method");

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "TOTP must never mint a session without a completed primary factor");
    }

    /// <summary>The request's host, and the tenant the middleware resolved for it, if any.</summary>
    private void OnHost(Guid? tenantId, string host = TenantHost)
    {
        _host = host;
        _tenantAccessor.Setup(a => a.IsResolved).Returns(tenantId.HasValue);
        _tenantAccessor.Setup(a => a.TenantId).Returns(tenantId ?? Guid.Empty);
    }

    private void TokenBoundTo(Guid subjectId, Guid? tenantId, bool admittedByInvite = false) =>
        _totpService
            .Setup(s => s.VerifyStepUpAsync("step-up", "123456"))
            .ReturnsAsync(new TotpLoginResult(subjectId, "rhys", "Rhys", tenantId, admittedByInvite));

    private void SetupMembership(Guid subjectId, Guid tenantId, bool isMember) =>
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subjectId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(isMember);

    private void SetupSessionIssuance() =>
        _sessionService
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 3600, Guid.Empty));

    private Task<ActionResult<TotpLoginResponse>> LoginAsync() =>
        CreateController().Login(new TotpLoginRequest { StepUpToken = "step-up", Code = "123456" });

    /// <summary>Signs a member in at the authenticator step and returns the Set-Cookie header.</summary>
    private async Task<string> SignInAsync(Guid subjectId, bool trustDevice, string? presentedCookie = null)
    {
        var tenantId = Guid.NewGuid();
        OnHost(tenantId);
        TokenBoundTo(subjectId, tenantId);
        SetupMembership(subjectId, tenantId, isMember: true);
        SetupSessionIssuance();

        var controller = CreateController();
        if (presentedCookie is not null)
            controller.HttpContext.Request.Headers.Cookie = presentedCookie;

        var result = await controller.Login(
            new TotpLoginRequest { StepUpToken = "step-up", Code = "123456", TrustDevice = trustDevice });

        result.Result.Should().BeOfType<OkObjectResult>();
        return controller.HttpContext.Response.Headers.SetCookie.ToString();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenAskedToTrustTheDevice_GivesItTheTrustedDeviceCookie()
    {
        var subjectId = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddDays(30);
        _trustedDevices
            .Setup(t => t.TrustAsync(subjectId, "old-token", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TrustedDeviceGrant(Guid.NewGuid(), "device-token", expiresAt));

        var setCookie = await SignInAsync(subjectId, trustDevice: true,
            presentedCookie: $"{SessionCookieExtensions.TrustedDeviceCookieName}=old-token");

        // Written last, so everything after its name is its own attributes.
        var deviceCookie = setCookie[setCookie.IndexOf(SessionCookieExtensions.TrustedDeviceCookieName, StringComparison.Ordinal)..];
        deviceCookie.Should().StartWith($"{SessionCookieExtensions.TrustedDeviceCookieName}=device-token;");
        deviceCookie.Should().Contain("httponly", "script must never read the token that replaces a second factor");
        deviceCookie.Should().Contain($"expires={expiresAt:ddd, dd MMM yyyy}");
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.DeviceTrustGranted, subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()));
    }

    /// <summary>
    /// Signing in over a session redeemed from a login code ends that session, so its refresh token
    /// does not stay live beside the person's own sign-in; signing in over an ordinary one leaves it.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_OverAHandedOffSession_EndsIt(bool handedOff)
    {
        var subjectId = Guid.NewGuid();
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(subjectId, "previous-session", handedOff));
        var audit = new Mock<IAuthAuditService>();
        _requestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(audit.Object).BuildServiceProvider();

        await SignInAsync(subjectId, trustDevice: false,
            presentedCookie: $"{new OidcOptions().Cookie.RefreshTokenName}=presented");

        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(subjectId, "previous-session", It.IsAny<string>()),
            handedOff ? Times.Once() : Times.Never());
        audit.Verify(
            a => a.LogAsync(AuthAuditEventType.Logout, subjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
                null, It.Is<string?>(d => d != null && d.Contains("replaced_by_sign_in")), null, null, null),
            handedOff ? Times.Once() : Times.Never());
    }

    /// <summary>
    /// The sign-in is complete, and logged, before the cookies it calls for are written, so the
    /// handed-off session it replaces is ended after the login row.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_OverAHandedOffSession_LogsTheSignInBeforeEndingTheReplacedSession()
    {
        var subjectId = Guid.NewGuid();
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ReturnsAsync(new RefreshTokenSession(subjectId, "previous-session", true));
        var events = new List<string>();
        _auditService
            .Setup(a => a.LogAsync(It.IsAny<string>(), subjectId, true, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()))
            .Callback((string type, Guid? _, bool _, string? _, string? _, string? _, string? _, Guid? _,
                AuthAuditActor? _, Guid? _) => events.Add(type))
            .Returns(Task.CompletedTask);
        _requestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(_auditService.Object).BuildServiceProvider();

        await SignInAsync(subjectId, trustDevice: false,
            presentedCookie: $"{new OidcOptions().Cookie.RefreshTokenName}=presented");

        events.Should().Equal(AuthAuditEventType.Login, AuthAuditEventType.Logout);
    }

    /// <summary>
    /// The sign-in is recorded before its cookies are written, so a failure ending the handed-off
    /// session leaves the success row and last-login behind an error answer. The session cookies
    /// already appended are never sent: the exception handler clears the unsent response.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenEndingTheHandedOffSessionFails_LeavesALoginSuccessRowAndLastLoginBehindTheError()
    {
        var subjectId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        OnHost(tenantId);
        TokenBoundTo(subjectId, tenantId);
        SetupMembership(subjectId, tenantId, isMember: true);
        SetupSessionIssuance();
        var refreshTokens = new Mock<IRefreshTokenService>();
        refreshTokens.Setup(r => r.FindOwnerAsync("presented"))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        _requestServices = new ServiceCollection()
            .AddSingleton(refreshTokens.Object).AddSingleton(_auditService.Object).BuildServiceProvider();

        var controller = CreateController();
        controller.HttpContext.Request.Headers.Cookie = $"{new OidcOptions().Cookie.RefreshTokenName}=presented";

        var act = () => controller.Login(new TotpLoginRequest { StepUpToken = "step-up", Code = "123456" });

        await act.Should().ThrowAsync<InvalidOperationException>();
        _subjectService.Verify(s => s.UpdateLastLoginAsync(subjectId), Times.Once);
        _auditService.Verify(a => a.LogAsync(
            AuthAuditEventType.Login, subjectId, true,
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<Guid?>(), It.IsAny<AuthAuditActor?>(), It.IsAny<Guid?>()), Times.Once);
        refreshTokens.Verify(
            r => r.RevokeSessionForSubjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenNotAskedToTrustTheDevice_TrustsNothing()
    {
        var setCookie = await SignInAsync(Guid.NewGuid(), trustDevice: false);

        setCookie.Should().NotContain(SessionCookieExtensions.TrustedDeviceCookieName);
        _trustedDevices.Verify(
            t => t.TrustAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenTheServiceTrustsNothing_SignsInWithoutTheCookie()
    {
        var subjectId = Guid.NewGuid();
        _trustedDevices
            .Setup(t => t.TrustAsync(subjectId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TrustedDeviceGrant?)null);

        var setCookie = await SignInAsync(subjectId, trustDevice: true);

        setCookie.Should().NotContain(SessionCookieExtensions.TrustedDeviceCookieName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WithARejectedCode_TrustsNothing()
    {
        _totpService
            .Setup(s => s.VerifyStepUpAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((TotpLoginResult?)null);

        await CreateController().Login(
            new TotpLoginRequest { StepUpToken = "step-up", Code = "000000", TrustDevice = true });

        _trustedDevices.Verify(
            t => t.TrustAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(30 * 24, 30)]
    [InlineData(12, 1)]
    [InlineData(0, 0)]
    [InlineData(-24, 0)]
    public void TrustedDevicePolicy_OffersTheOptionExactlyWhenTheLifetimeIsAboveZero(int lifetimeHours, int expected)
    {
        _trustedDevices.Setup(t => t.Lifetime).Returns(TimeSpan.FromHours(lifetimeHours));

        var result = CreateController().GetTrustedDevicePolicy();

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<TrustedDevicePolicyResponse>()
            .Which.Days.Should().Be(expected);
    }

    private void AssertDeniedAsNotAMember(ActionResult<TotpLoginResponse> result)
    {
        var problem = result.Result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(403);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be(TotpController.NotAMember);

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void AssertSessionIssued(ActionResult<TotpLoginResponse> result, Guid subjectId)
    {
        result.Result.Should().BeOfType<OkObjectResult>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenSubjectIsMemberOfTenant_IssuesSession()
    {
        var subjectId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        OnHost(tenantId);
        TokenBoundTo(subjectId, tenantId);
        SetupMembership(subjectId, tenantId, isMember: true);
        SetupSessionIssuance();

        AssertSessionIssued(await LoginAsync(), subjectId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenTheMembershipIsGoneSinceThePrimaryFactor_IsDeniedAsNotAMember()
    {
        var subjectId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        OnHost(tenantId);
        TokenBoundTo(subjectId, tenantId);
        SetupMembership(subjectId, tenantId, isMember: false);

        AssertDeniedAsNotAMember(await LoginAsync());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_ForAnInviteeTheIdentityProviderAdmitted_IssuesSessionWithoutMembership()
    {
        var subjectId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        OnHost(tenantId);
        TokenBoundTo(subjectId, tenantId, admittedByInvite: true);
        SetupMembership(subjectId, tenantId, isMember: false);
        SetupSessionIssuance();

        AssertSessionIssued(await LoginAsync(), subjectId);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(Apex)]
    [InlineData(DashboardHost)]
    public async Task Login_OnATenantlessHostWithATokenMintedThere_IssuesSession(string host)
    {
        var subjectId = Guid.NewGuid();
        OnHost(null, host);
        TokenBoundTo(subjectId, tenantId: null);
        SetupSessionIssuance();

        AssertSessionIssued(await LoginAsync(), subjectId);
        _tenantMemberService.Verify(
            t => t.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// The passkey ceremony on a single-tenant apex resolves the sole tenant, and so does the
    /// authenticator step that follows it there.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_OnASingleTenantApexAfterAPasskey_IssuesSession()
    {
        var subjectId = Guid.NewGuid();
        var soleTenant = Guid.NewGuid();
        OnHost(soleTenant, Apex);
        TokenBoundTo(subjectId, soleTenant);
        SetupMembership(subjectId, soleTenant, isMember: true);
        SetupSessionIssuance();

        AssertSessionIssued(await LoginAsync(), subjectId);
    }

    /// <summary>
    /// The OIDC callback on a single-tenant apex runs without a tenant and binds none, while the
    /// authenticator step there resolves the sole tenant. It is the same host, and the callback
    /// checked no membership, so neither does this.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_OnASingleTenantApexAfterAnIdentityProvider_IssuesSession()
    {
        var subjectId = Guid.NewGuid();
        OnHost(Guid.NewGuid(), Apex);
        TokenBoundTo(subjectId, tenantId: null);
        SetupSessionIssuance();

        AssertSessionIssued(await LoginAsync(), subjectId);
        _tenantMemberService.Verify(
            t => t.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public static TheoryData<Guid?, Guid?, string> MismatchedHosts()
    {
        var tenant = Guid.NewGuid();
        return new TheoryData<Guid?, Guid?, string>
        {
            // Minted on a tenant, redeemed on a multi-tenant apex or a dashboard host.
            { tenant, null, Apex },
            { tenant, null, DashboardHost },
            // Minted with no tenant, redeemed on a tenant's own subdomain.
            { null, tenant, TenantHost },
            // Minted on one tenant, redeemed on another, including another's sole-tenant apex.
            { tenant, Guid.NewGuid(), TenantHost },
            { tenant, Guid.NewGuid(), Apex },
        };
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(MismatchedHosts))]
    public async Task Login_OnAHostOtherThanTheOneTheTokenWasMintedFor_IsDenied(
        Guid? mintedFor, Guid? hostTenant, string host)
    {
        var subjectId = Guid.NewGuid();
        OnHost(hostTenant, host);
        TokenBoundTo(subjectId, mintedFor, admittedByInvite: true);
        if (hostTenant is { } resolved)
            SetupMembership(subjectId, resolved, isMember: true);

        AssertDeniedAsNotAMember(await LoginAsync());
    }
}
