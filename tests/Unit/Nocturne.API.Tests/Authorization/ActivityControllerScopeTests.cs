using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Asserts that <c>ActivityController</c> actually calls
/// <see cref="Nocturne.API.Authorization.ActivityWriteScopeGuard"/>. <c>ActivityWriteScopeGuardTests</c>
/// exercises the guard function in isolation, so removing the call from the handler would leave both
/// that suite and the attribute sweep in <see cref="V4WriteScopeGatingTests"/> green while the
/// endpoint became ungated — the activity endpoints are exempted from the sweep precisely because
/// their gate is a method call.
/// </summary>
public class ActivityControllerScopeTests
{
    private const string HeartRateRecordType = "heartrate";

    [Fact]
    public async Task CreateActivities_DeniesARecordWhoseCategoryScopeIsMissing()
    {
        var (controller, service) = Build(
            requiredScope: OAuthScopes.HeartRateReadWrite,
            grantedScopes: [OAuthScopes.TreatmentsReadWrite]);

        var result = await controller.CreateActivities(
            [new UpsertActivityRequest { Mills = 1, Type = HeartRateRecordType }]);

        Denied(result.Result);
        service.Verify(
            s => s.CreateActivitiesAsync(It.IsAny<List<Activity>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateActivities_AllowsARecordWhoseCategoryScopeIsHeld()
    {
        var (controller, service) = Build(
            requiredScope: OAuthScopes.HeartRateReadWrite,
            grantedScopes: [OAuthScopes.HeartRateReadWrite]);
        service.Setup(s => s.CreateActivitiesAsync(It.IsAny<List<Activity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await controller.CreateActivities(
            [new UpsertActivityRequest { Mills = 1, Type = HeartRateRecordType }]);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task CreateActivities_AllowsARecordThatNeedsNoCategoryScope()
    {
        // A plain activity has no dedicated destination table, so the decomposer returns null and
        // the guard must not invent a requirement.
        var (controller, service) = Build(requiredScope: null, grantedScopes: []);
        service.Setup(s => s.CreateActivitiesAsync(It.IsAny<List<Activity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await controller.CreateActivities(
            [new UpsertActivityRequest { Mills = 1, Type = "walk" }]);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    private static void Denied(object? result) =>
        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

    private static (ActivityController Controller, Mock<IActivityService> Service) Build(
        string? requiredScope, string[] grantedScopes)
    {
        var service = new Mock<IActivityService>(MockBehavior.Strict);
        var decomposer = new Mock<IActivityDecomposer>(MockBehavior.Loose);
        decomposer.Setup(d => d.RequiredWriteScope(It.IsAny<Activity>())).Returns(requiredScope);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);

        var controller = new ActivityController(service.Object, decomposer.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, service);
    }
}
