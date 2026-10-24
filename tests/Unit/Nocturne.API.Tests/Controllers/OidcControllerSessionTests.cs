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

public class OidcControllerSessionTests
{
    private readonly Mock<ITenantMemberService> _tenantMemberService = new();
    private readonly OidcController _controller;

    public OidcControllerSessionTests()
    {
        _controller = new OidcController(
            Mock.Of<IOidcAuthService>(),
            Mock.Of<IOidcProviderService>(),
            Mock.Of<ISubjectService>(),
            Mock.Of<IAuthAuditService>(),
            _tenantMemberService.Object,
            Options.Create(new OidcOptions()),
            Options.Create(new BaseDomainOptions { BaseDomain = "nocturne.example.com" }),
            NullLogger<OidcController>.Instance);
        _controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
    }

    private async Task<SessionInfo> SessionFor(AuthContext authContext)
    {
        _controller.HttpContext.Items["AuthContext"] = authContext;
        var result = await _controller.GetSession();
        return result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SessionInfo>().Subject;
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetSession_ReportsWhetherTheSubjectHasMultipleTenants(bool multiple)
    {
        var subjectId = Guid.NewGuid();
        _tenantMemberService
            .Setup(s => s.HasMultipleActiveTenantsAsync(subjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(multiple);

        var session = await SessionFor(new AuthContext
        {
            IsAuthenticated = true,
            SubjectId = subjectId,
            AuthType = AuthType.PlatformAccess,
        });

        session.HasMultipleTenants.Should().Be(multiple);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSession_ForACredentialWithNoSubject_HasNoTenantsToSwitchBetween()
    {
        var session = await SessionFor(new AuthContext { IsAuthenticated = true });

        session.HasMultipleTenants.Should().BeFalse();
        _tenantMemberService.Verify(
            s => s.HasMultipleActiveTenantsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetSession_WhenUnauthenticated_ReadsNoMemberships()
    {
        var session = await SessionFor(AuthContext.Unauthenticated());

        session.HasMultipleTenants.Should().BeFalse();
        _tenantMemberService.VerifyNoOtherCalls();
    }
}
