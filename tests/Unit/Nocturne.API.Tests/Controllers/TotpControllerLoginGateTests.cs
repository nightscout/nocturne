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
/// Tests for the cross-tenant guard on TOTP login. <see cref="ITotpService.VerifyLoginAsync"/>
/// resolves the subject by username globally, so the controller must verify the resolved
/// subject is a member of the tenant being logged into before issuing a session.
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
    public async Task Login_WhenSubjectIsNotMemberOfTenant_DeniesWithoutIssuingSession()
    {
        var subjectId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        _totpService
            .Setup(s => s.VerifyLoginAsync("rhys", "123456"))
            .ReturnsAsync(new TotpLoginResult(subjectId, "rhys", "Rhys"));
        _tenantAccessor.Setup(a => a.TenantId).Returns(tenantId);
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subjectId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var controller = CreateController();
        var result = await controller.Login(new TotpLoginRequest { Username = "rhys", Code = "123456" });

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400, "a non-member must be rejected like an invalid code");

        _sessionService.Verify(
            s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a TOTP login from a non-member must not mint a session");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Login_WhenSubjectIsMemberOfTenant_IssuesSession()
    {
        var subjectId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        _totpService
            .Setup(s => s.VerifyLoginAsync("rhys", "123456"))
            .ReturnsAsync(new TotpLoginResult(subjectId, "rhys", "Rhys"));
        _tenantAccessor.Setup(a => a.TenantId).Returns(tenantId);
        _tenantMemberService
            .Setup(t => t.IsMemberAsync(subjectId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _sessionService
            .Setup(s => s.IssueSessionAsync(It.IsAny<Guid>(), It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionTokenPair("access-token", "refresh-token", 3600));

        var controller = CreateController();
        var result = await controller.Login(new TotpLoginRequest { Username = "rhys", Code = "123456" });

        result.Result.Should().BeOfType<OkObjectResult>();
        _sessionService.Verify(
            s => s.IssueSessionAsync(subjectId, It.IsAny<SessionContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
