using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.Authentication;
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

    private TotpController CreateController()
        => new(
            _totpService.Object,
            _sessionService.Object,
            _subjectService.Object,
            _auditService.Object,
            _tenantAccessor.Object,
            _tenantMemberService.Object,
            Options.Create(new OidcOptions()),
            NullLogger<TotpController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
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

    private void OnHost(Guid? tenantId)
    {
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
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 3600));

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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_OnATenantlessHostWithATokenMintedThere_IssuesSession()
    {
        var subjectId = Guid.NewGuid();
        OnHost(null);
        TokenBoundTo(subjectId, tenantId: null);
        SetupSessionIssuance();

        AssertSessionIssued(await LoginAsync(), subjectId);
        _tenantMemberService.Verify(
            t => t.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public static TheoryData<Guid?, Guid?> MismatchedHosts()
    {
        var tenant = Guid.NewGuid();
        return new TheoryData<Guid?, Guid?>
        {
            { tenant, null },
            { null, tenant },
            { tenant, Guid.NewGuid() },
        };
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(MismatchedHosts))]
    public async Task Login_OnAHostOtherThanTheOneTheTokenWasMintedFor_IsDenied(
        Guid? mintedFor, Guid? host)
    {
        var subjectId = Guid.NewGuid();
        OnHost(host);
        TokenBoundTo(subjectId, mintedFor, admittedByInvite: true);
        if (host is { } hostTenant)
            SetupMembership(subjectId, hostTenant, isMember: true);

        AssertDeniedAsNotAMember(await LoginAsync());
    }
}
