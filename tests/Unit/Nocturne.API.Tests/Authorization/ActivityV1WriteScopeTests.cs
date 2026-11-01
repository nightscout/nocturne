using Nocturne.API.Services.Health;
using System.Reflection;
using System.Text.Json;
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
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// A v1 activity write is admitted by any of the category write scopes and then gated per record,
/// so a key holding only a heart rate or step count scope can upload its own data and nothing else.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityV1WriteScopeTests
{
    private static readonly string[] WriteActions =
    [
        nameof(ActivityController.CreateActivities),
        nameof(ActivityController.SaveActivities),
        nameof(ActivityController.UpdateActivity),
    ];

    private static bool Admits(string action, params string[] grantedScopes)
    {
        var attribute = typeof(ActivityController).GetMethod(action)!
            .GetCustomAttribute<RequireScopeAttribute>()!;
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext { IsAuthenticated = true };
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(grantedScopes);
        var filterContext = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());

        attribute.OnAuthorization(filterContext);

        return filterContext.Result is null;
    }

    [Theory]
    [InlineData(nameof(ActivityController.CreateActivities), Scope.HeartRateReadWrite)]
    [InlineData(nameof(ActivityController.CreateActivities), Scope.StepCountReadWrite)]
    [InlineData(nameof(ActivityController.CreateActivities), Scope.TreatmentsReadWrite)]
    [InlineData(nameof(ActivityController.SaveActivities), Scope.HeartRateReadWrite)]
    [InlineData(nameof(ActivityController.SaveActivities), Scope.StepCountReadWrite)]
    [InlineData(nameof(ActivityController.UpdateActivity), Scope.HeartRateReadWrite)]
    [InlineData(nameof(ActivityController.UpdateActivity), Scope.StepCountReadWrite)]
    public void WriteAction_AdmitsACallerHoldingAnyOneCategoryWriteScope(string action, string scope)
    {
        Admits(action, scope).Should().BeTrue();
    }

    [Theory]
    [InlineData(nameof(ActivityController.CreateActivities))]
    [InlineData(nameof(ActivityController.SaveActivities))]
    [InlineData(nameof(ActivityController.UpdateActivity))]
    public void WriteAction_RefusesACallerHoldingNoCategoryWriteScope(string action)
    {
        Admits(action, Scope.GlucoseReadWrite, Scope.HeartRateRead, Scope.TreatmentsRead)
            .Should().BeFalse();
    }

    [Fact]
    public void DeleteAction_StillNeedsFullAccess()
    {
        Admits(nameof(ActivityController.DeleteActivity), Scope.HeartRateReadWrite).Should().BeFalse();
    }

    [Fact]
    public void EveryWriteActionRunsTheSameAdmission()
    {
        var admissions = WriteActions
            .Select(a => typeof(ActivityController).GetMethod(a)!
                .GetCustomAttribute<RequireScopeAttribute>()!.RequiredScopes)
            .ToList();

        admissions.Should().AllSatisfy(scopes => scopes.Should().BeEquivalentTo(
            [Scope.TreatmentsReadWrite, Scope.HeartRateReadWrite, Scope.StepCountReadWrite, Scope.SleepReadWrite]));
    }

    [Fact]
    public async Task UpdateActivity_HeartRateOnlyKeyOverAnExistingExercise_IsRefusedForTreatments()
    {
        var service = new Mock<IActivityService>();
        service.Setup(s => s.GetStoredActivitiesAsync("a1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Activity { Id = "a1", Type = "exercise" }]);
        var decomposer = new Mock<IActivityDecomposer>();
        decomposer.Setup(d => d.RequiredWriteScope(It.IsAny<Activity>()))
            .Returns((Activity a) => a.Type == "heartrate" ? Scope.HeartRateReadWrite : Scope.TreatmentsReadWrite);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>([Scope.HeartRateReadWrite]);
        var controller = new ActivityController(
            service.Object, decomposer.Object, NullLogger<ActivityController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.UpdateActivity(
            "a1", new Activity { Type = "heartrate" }, CancellationToken.None);

        var forbidden = result.Result.Should().BeOfType<ObjectResult>().Subject;
        forbidden.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonSerializer.Serialize(forbidden.Value).Should().Contain(Scope.TreatmentsReadWrite);
        service.Verify(
            s => s.UpdateActivityAsync(It.IsAny<string>(), It.IsAny<Activity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("post", new[] { Scope.HeartRateReadWrite }, "heartrate", true)]
    [InlineData("post", new[] { Scope.HeartRateReadWrite }, "exercise", false)]
    [InlineData("post", new[] { Scope.TreatmentsReadWrite }, "heartrate", false)]
    [InlineData("post", new[] { Scope.TreatmentsReadWrite }, "steps-total", false)]
    [InlineData("post", new[] { Scope.StepCountReadWrite }, "steps-total", true)]
    [InlineData("post", new[] { Scope.HeartRateReadWrite, Scope.TreatmentsReadWrite }, "mixed", true)]
    [InlineData("post", new[] { Scope.HeartRateReadWrite }, "mixed", false)]
    [InlineData("post", new[] { Scope.TreatmentsReadWrite }, "mixed", false)]
    [InlineData("put", new[] { Scope.HeartRateReadWrite }, "heartrate", true)]
    [InlineData("put", new[] { Scope.HeartRateReadWrite }, "exercise", false)]
    [InlineData("put", new[] { Scope.TreatmentsReadWrite }, "heartrate", false)]
    [InlineData("putById", new[] { Scope.HeartRateReadWrite }, "heartrate", true)]
    [InlineData("putById", new[] { Scope.HeartRateReadWrite }, "exercise", false)]
    [InlineData("putById", new[] { Scope.TreatmentsReadWrite }, "heartrate", false)]
    public async Task Write_IsGatedPerRecord(string route, string[] granted, string body, bool allowed)
    {
        var service = new Mock<IActivityService>();
        service.Setup(s => s.GetStoredActivitiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        service.Setup(s => s.CreateActivitiesAsync(It.IsAny<IEnumerable<Activity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Activity> a, CancellationToken _) => a.ToList());
        service.Setup(s => s.UpdateActivityAsync(It.IsAny<string>(), It.IsAny<Activity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Activity a, CancellationToken _) => a);
        var decomposer = new Mock<IActivityDecomposer>();
        decomposer.Setup(d => d.RequiredWriteScope(It.IsAny<Activity>()))
            .Returns((Activity a) => a.Type == "heartrate" ? Scope.HeartRateReadWrite
                : a.Type == "steps-total" ? Scope.StepCountReadWrite
                : Scope.TreatmentsReadWrite);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)new HashSet<string>(granted);
        var controller = new ActivityController(
            service.Object, decomposer.Object, NullLogger<ActivityController>.Instance, TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var records = body == "mixed"
            ? new[] { new Activity { Type = "heartrate" }, new Activity { Type = "exercise" } }
            : [new Activity { Type = body }];
        IActionResult? result = route switch
        {
            "post" => (await controller.CreateActivities(
                JsonSerializer.SerializeToElement(records), CancellationToken.None)).Result,
            "put" => (await controller.SaveActivities(
                JsonSerializer.SerializeToElement(records[0]), CancellationToken.None)).Result,
            _ => (await controller.UpdateActivity("a1", records[0], CancellationToken.None)).Result,
        };

        if (allowed)
            result.Should().BeOfType<OkObjectResult>();
        else
            result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }
}
