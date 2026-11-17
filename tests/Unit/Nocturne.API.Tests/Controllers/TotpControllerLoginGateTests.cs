using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
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
    private readonly Mock<ISessionService> _sessionService = new();
    private readonly Mock<ISubjectService> _subjectService = new();
    private readonly Mock<IAuthAuditService> _auditService = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();

    private const string Apex = "nocturne.example.com";
    private const string TenantHost = "erik.nocturne.example.com";
    private const string DashboardHost = "app.nocturne.example.com";
    private string _host = TenantHost;

    private TotpController CreateController()
        => new(
            _totpService.Object,
            _sessionService.Object,
            _subjectService.Object,
            _auditService.Object,
            _tenantAccessor.Object,
            _tenantMemberService.Object,
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = Apex }),
            NullLogger<TotpController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { Request = { Host = new HostString(_host) } },
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
