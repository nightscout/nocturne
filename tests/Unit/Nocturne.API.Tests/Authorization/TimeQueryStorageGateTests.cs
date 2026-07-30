using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Drives the actions, not the attribute set. The gate is a call inside the handler because the
/// collection is a route or query value, so a scan for attributes cannot see it and a mapping test
/// would still pass with the call deleted.
/// </summary>
public class TimeQueryStorageGateTests
{
    [Theory]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [InlineData("profile")]
    [InlineData("food")]
    public async Task Slice_RefusesACollectionOutsideTheGrant(string storage)
    {
        // A glucose-only grant reaching /api/v1/slice/treatments/... returned every treatment
        // record while the route carried a class-level OR of glucose|treatments|devices.
        var (controller, service) = Build(OAuthScopes.GlucoseRead);

        var result = await controller.GetSlicedData(storage, "dateString");

        Refused(result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Slice_AllowsTheCollectionTheGrantCovers()
    {
        var (controller, service) = Build(OAuthScopes.GlucoseRead);
        service
            .Setup(s => s.ExecuteSliceQueryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await controller.GetSlicedData("entries", "dateString");

        result.Should().NotBeOfType<ObjectResult>();
    }

    [Fact]
    public async Task Slice_RefusesAnUnknownCollection()
    {
        var (controller, service) = Build(OAuthScopes.FullAccess);

        var result = await controller.GetSlicedData("sensor_glucose", "dateString");

        result.Should().BeOfType<BadRequestObjectResult>();
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    public void TimesEcho_RefusesACollectionOutsideTheGrant(string storage)
    {
        // The echo variants take the collection as a query value and reach the same service.
        var (controller, service) = Build(OAuthScopes.GlucoseRead);

        var result = controller.GetTimeQueryEchoWithPrefix("2026-07", storage: storage);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FullAccess_ReadsEveryCollection()
    {
        var (controller, service) = Build(OAuthScopes.FullAccess);
        service
            .Setup(s => s.ExecuteSliceQueryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        foreach (var storage in new[] { "entries", "treatments", "devicestatus", "profile", "food" })
        {
            var result = await controller.GetSlicedData(storage, "dateString");
            result.Should().NotBeOfType<ObjectResult>(storage);
        }
    }

    private static void Refused(ActionResult result) =>
        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

    private static (TimeQueryController Controller, Mock<ITimeQueryService> Service) Build(
        params string[] grantedScopes)
    {
        var service = new Mock<ITimeQueryService>(MockBehavior.Loose);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = OAuthScopes.Normalize(grantedScopes);

        var controller = new TimeQueryController(service.Object, NullLogger<TimeQueryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, service);
    }
}
