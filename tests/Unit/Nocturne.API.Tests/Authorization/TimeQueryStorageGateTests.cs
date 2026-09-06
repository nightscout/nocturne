using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Drives the actions rather than the attribute set. The <c>slice</c> and <c>echo</c> gates are
/// calls inside the handler because the collection is a route or query value, so a scan for
/// attributes cannot see them and a mapping test would still pass with the call deleted.
/// </summary>
public class TimeQueryStorageGateTests
{
    /// <summary>The collections <c>slice</c> dispatches to; the rest are rejected as bad input.</summary>
    private static readonly string[] SliceableStorage = ["entries", "treatments", "devicestatus"];

    [Theory]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    public async Task Slice_RefusesACollectionOutsideTheGrant(string storage)
    {
        // A class-level OR of glucose|treatments|devices would admit this caller to
        // /api/v1/slice/treatments/... and every treatment record with it.
        var (controller, service) = Build(Scope.GlucoseRead);

        var result = await controller.GetSlicedData(storage, "dateString");

        Refused(result);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("entries", Scope.GlucoseRead)]
    [InlineData("treatments", Scope.TreatmentsRead)]
    [InlineData("devicestatus", Scope.DevicesRead)]
    public async Task Slice_AllowsTheCollectionTheGrantCovers(string storage, string scope)
    {
        var (controller, service) = Build(scope);
        StubSlice(service);

        var result = await controller.GetSlicedData(storage, "dateString");

        result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("sensor_glucose")]
    [InlineData("profile")]
    [InlineData("food")]
    public async Task Slice_RefusesACollectionItDoesNotServe(string storage)
    {
        // Ahead of the scope gate, so the answer does not vary with the grant on a collection the
        // route never served.
        foreach (var scopes in new[] { new[] { Scope.FullAccess }, new[] { Scope.SleepRead } })
        {
            var (controller, service) = Build(scopes);

            var result = await controller.GetSlicedData(storage, "dateString");

            result.Should().BeOfType<BadRequestObjectResult>(storage);
            service.VerifyNoOtherCalls();
        }
    }

    [Theory]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    public void TimesEcho_RefusesACollectionOutsideTheGrant(string storage)
    {
        // The echo variants take the collection as a query value and reach the same service.
        var (controller, service) = Build(Scope.GlucoseRead);

        var result = controller.GetTimeQueryEchoWithPrefix("2026-07", storage: storage);

        result.Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FullAccess_ReadsEverySliceableCollection()
    {
        var (controller, service) = Build(Scope.FullAccess);
        StubSlice(service);

        foreach (var storage in SliceableStorage)
        {
            var result = await controller.GetSlicedData(storage, "dateString");
            result.Should().BeOfType<OkObjectResult>(storage);
        }
    }

    /// <summary>
    /// The <c>times</c> actions take no collection selector — the internal call hardcodes
    /// <c>entries</c> — so their gate is an attribute rather than a call in the handler, and the
    /// category it names has to be the one they actually read.
    /// </summary>
    [Theory]
    [InlineData(nameof(TimeQueryController.GetTimeBasedEntries))]
    [InlineData(nameof(TimeQueryController.GetTimeBasedEntriesWithPrefix))]
    [InlineData(nameof(TimeQueryController.GetTimeBasedEntriesWithPrefixAndRegex))]
    public void Times_RequiresGlucoseRead(string actionName)
    {
        var action = typeof(TimeQueryController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        action.Should().NotBeNull();

        var required = action!.GetCustomAttributes<RequireScopeAttribute>()
            .SelectMany(a => a.RequiredScopes)
            .ToArray();

        required.Should().Equal(Scope.GlucoseRead);
    }

    /// <summary>
    /// Runs the attributes the action actually carries against a grant holding no glucose scope.
    /// The scopes are ones that do not expand to glucose; <c>health.read</c> does, so a grant
    /// carrying it reads entries legitimately.
    /// </summary>
    [Theory]
    [InlineData(nameof(TimeQueryController.GetTimeBasedEntriesWithPrefix))]
    [InlineData(nameof(TimeQueryController.GetTimeBasedEntriesWithPrefixAndRegex))]
    public void Times_RefusesAGrantWithoutGlucose(string actionName)
    {
        var action = typeof(TimeQueryController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!;
        var attributes = action.GetCustomAttributes<RequireScopeAttribute>().ToArray();
        attributes.Should().NotBeEmpty();

        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = Scope.Normalize(
            [Scope.SleepRead, Scope.ReportsRead]);

        var filterContext = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());

        foreach (var attribute in attributes)
            attribute.OnAuthorization(filterContext);

        filterContext.Result.Should().BeOfType<ForbidResult>();
    }

    private static void StubSlice(Mock<ITimeQueryService> service) =>
        service
            .Setup(s => s.ExecuteSliceQueryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<Dictionary<string, object>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

    private static void Refused(ActionResult result) =>
        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

    private static (TimeQueryController Controller, Mock<ITimeQueryService> Service) Build(
        params string[] grantedScopes)
    {
        var service = new Mock<ITimeQueryService>(MockBehavior.Loose);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = Scope.Normalize(grantedScopes);

        var controller = new TimeQueryController(service.Object, NullLogger<TimeQueryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        return (controller, service);
    }
}
