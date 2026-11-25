using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.Authentication;
using Nocturne.API.Extensions;
using Nocturne.API.Middleware;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// Only a caller who manages the members and whose own access does not end may keep a device
/// credential past the date it stops; a caller whose access ends is refused before anything changes.
/// </summary>
[Trait("Category", "Unit")]
public class DirectGrantControllerKeepTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Caller = Guid.CreateVersion7();

    private readonly Mock<IDirectGrantService> _grants = new();
    private AuthType _authType = AuthType.SessionCookie;

    private DirectGrantController Controller(DateTime? callerAccessEndsAt, params string[] scopes)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = TestRequestServices.Build(clamped: false),
        };
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = _authType,
            SubjectId = Caller,
            TenantId = Tenant,
            SessionId = LiveSession.Id,
        };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)scopes.ToHashSet();
        new RequestMembership(
                Caller, Tenant,
                new MembershipFacts(Guid.CreateVersion7(), new TenantMemberAccess(scopes.ToHashSet(), false, callerAccessEndsAt), new TenantMemberAccess(scopes.ToHashSet(), false, callerAccessEndsAt), null))
            .StoreOn(httpContext);

        return new DirectGrantController(Mock.Of<ISubjectRepository>(), _grants.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    [Fact]
    public async Task AMemberManagerWhoseAccessDoesNotEndKeepsTheCredential()
    {
        var grantId = Guid.CreateVersion7();
        _grants.Setup(g => g.KeepDeviceCredentialAsync(grantId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Controller(null, Scope.MembersManage).Keep(grantId);

        result.Should().BeOfType<NoContentResult>();
        _grants.Verify(g => g.KeepDeviceCredentialAsync(grantId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnInactiveCallerIsRefusedAndNothingIsKept()
    {
        var strict = new Mock<IDirectGrantService>(MockBehavior.Strict);
        var controller = Controller(null, Scope.MembersManage);
        new RequestMembership(Caller, Tenant, null) { SubjectIsInactive = true }
            .StoreOn(controller.ControllerContext.HttpContext);
        controller = new DirectGrantController(Mock.Of<ISubjectRepository>(), strict.Object)
        {
            ControllerContext = controller.ControllerContext,
        };

        var result = await controller.Keep(Guid.CreateVersion7());

        result.Should().BeOfType<ChallengeResult>();
        strict.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ACallerWhoseAccessEndsIsRefusedAndNothingIsKept()
    {
        var result = await Controller(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), Scope.MembersManage)
            .Keep(Guid.CreateVersion7());

        var problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be(HttpContextExtensions.KeepDeviceCredentialEndDateDetail);
        _grants.Verify(g => g.KeepDeviceCredentialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACallerThatIsNotAnInteractiveSessionIsRefusedAndNothingIsKept()
    {
        _authType = AuthType.ApiKey;

        var result = await Controller(null, Scope.MembersManage).Keep(Guid.CreateVersion7());

        result.Should().BeOfType<ForbidResult>();
        _grants.Verify(g => g.KeepDeviceCredentialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AMixedMemberWhoseDirectReasonEndsIsRefusedEvenWhenTheSumDoesNot()
    {
        var controller = Controller(null, Scope.MembersManage);
        controller.HttpContext.SetGrantCeiling(
            new GrantCeiling(new HashSet<string> { Scope.MembersManage }, new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), false));

        var result = await controller.Keep(Guid.CreateVersion7());

        result.Should().BeOfType<ObjectResult>().Which.Value.Should().BeOfType<ProblemDetails>()
            .Which.Detail.Should().Be(HttpContextExtensions.KeepDeviceCredentialEndDateDetail);
        _grants.Verify(g => g.KeepDeviceCredentialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AMixedMemberWhoseDirectReasonDoesNotManageMembersIsRefused()
    {
        var controller = Controller(null, Scope.MembersManage);
        controller.HttpContext.SetGrantCeiling(new GrantCeiling(new HashSet<string> { Scope.GlucoseRead }, null, false));

        (await controller.Keep(Guid.CreateVersion7())).Should().BeOfType<ForbidResult>();
        _grants.Verify(g => g.KeepDeviceCredentialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ATeamOnlyMemberIsRefusedAndNothingIsKept()
    {
        var controller = Controller(null, Scope.MembersManage);
        controller.HttpContext.SetGrantCeiling(null);

        GrantCeilingAssertions.ShouldRefuseAsAccessFromTeam(await controller.Keep(Guid.CreateVersion7()));
        _grants.Verify(g => g.KeepDeviceCredentialAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACredentialThatIsNotADeviceCredentialIsNotFound()
    {
        var result = await Controller(null, Scope.MembersManage).Keep(Guid.CreateVersion7());

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }
}
