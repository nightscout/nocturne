using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// Asserts that <c>ActivityController</c> actually calls
/// <see cref="Nocturne.API.Authorization.ActivityWriteScopeGuard"/> and
/// <see cref="Nocturne.API.Authorization.ActivityReadScopeGuard"/>. The guard suites exercise those
/// functions in isolation, so removing a call from a handler would leave them and the attribute
/// sweep in <see cref="V4WriteScopeGatingTests"/> green while the endpoint became ungated — the
/// activity endpoints are exempted from the sweep precisely because their gate is a method call.
/// </summary>
public class ActivityControllerScopeTests
{
    private const string HeartRateRecordType = "heartrate";

    [Fact]
    public async Task CreateActivities_DeniesARecordWhoseCategoryScopeIsMissing()
    {
        var (controller, service) = Build(
            requiredScope: Scope.HeartRateReadWrite,
            grantedScopes: [Scope.TreatmentsReadWrite]);

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
            requiredScope: Scope.HeartRateReadWrite,
            grantedScopes: [Scope.HeartRateReadWrite]);
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

    [Fact]
    public async Task GetActivities_DropsRecordsWhoseCategoryScopeIsMissing()
    {
        var (controller, service) = BuildRead(grantedScopes: [Scope.HeartRateRead]);
        StubPage(service, OneRecordPerCategory());
        StubCounts(service);

        var result = await controller.GetActivities();

        Payload(result).Data.Select(a => a.Id).Should().Equal("hr");
    }

    [Fact]
    public async Task GetActivities_TotalsOnlyTheCategoriesTheCallerHolds()
    {
        var (controller, service) = BuildRead(grantedScopes: [Scope.HeartRateRead]);
        StubPage(service, OneRecordPerCategory());
        StubCounts(service);

        var result = await controller.GetActivities();

        Payload(result).Pagination.Total.Should().Be((int)CategoryCounts[Scope.HeartRateRead]);
    }

    [Fact]
    public async Task GetActivities_TotalsEveryCategoryForACallerHoldingThemAll()
    {
        var (controller, service) = BuildRead(grantedScopes: [.. CategoryScopes.Values]);
        StubPage(service, OneRecordPerCategory());
        StubCounts(service);

        var result = await controller.GetActivities();

        Payload(result).Pagination.Total.Should().Be((int)CategoryCounts.Values.Sum());
    }

    [Fact]
    public async Task GetActivities_AsksTheCountForNoCategoryTheCallerLacks()
    {
        var (controller, service) = BuildRead(grantedScopes: [Scope.HeartRateRead]);
        StubPage(service, OneRecordPerCategory());
        StubCounts(service);

        await controller.GetActivities();

        service.Verify(
            s => s.CountActivitiesByCategoryAsync(
                It.Is<IReadOnlySet<string>>(asked =>
                    asked.SetEquals(new[] { Scope.HeartRateRead })),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The total counts categories, not the page, so a page carrying nothing the caller may see
    /// must still report the records waiting on later pages — otherwise a client paging while
    /// <c>offset &lt; total</c> stops at the first such page.
    /// </summary>
    [Fact]
    public async Task GetActivities_TotalsTheHeldCategoriesEvenWhenThePageShowsNoneOfThem()
    {
        var (controller, service) = BuildRead(grantedScopes: [Scope.HeartRateRead]);
        StubPage(service, [ActivityWithId("sleep"), ActivityWithId("regular")]);
        StubCounts(service);

        var result = await controller.GetActivities(offset: 20);

        Payload(result).Data.Should().BeEmpty();
        Payload(result).Pagination.Total.Should().Be((int)CategoryCounts[Scope.HeartRateRead]);
    }

    [Fact]
    public async Task GetActivity_HidesARecordWhoseCategoryScopeIsMissing()
    {
        var (controller, service) = BuildRead(grantedScopes: [Scope.HeartRateRead]);
        service.Setup(s => s.GetActivityByIdAsync("sleep", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActivityWithId("sleep"));

        var result = await controller.GetActivity("sleep");

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetActivity_ReturnsARecordWhoseCategoryScopeIsHeld()
    {
        var (controller, service) = BuildRead(grantedScopes: [Scope.SleepRead]);
        service.Setup(s => s.GetActivityByIdAsync("sleep", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActivityWithId("sleep"));

        var result = await controller.GetActivity("sleep");

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Activity>()
            .Which.Id.Should().Be("sleep");
    }

    /// <summary>
    /// The in-handler filter decides what a response may contain, but only the attribute keeps a
    /// caller holding none of the four categories out — and a controller-level <c>[Authorize]</c>
    /// alone admits any member.
    /// </summary>
    [Theory]
    [InlineData(nameof(ActivityController.GetActivities))]
    [InlineData(nameof(ActivityController.GetActivity))]
    public void ReadActions_AdmitOnAnyMergedActivityCategory(string action)
    {
        var attribute = typeof(ActivityController).GetMethod(action)!
            .GetCustomAttribute<RequireScopeAttribute>();

        attribute.Should().NotBeNull($"{action} must be gated on the activity read scopes");
        attribute!.Scopes.Should().BeEquivalentTo(ActivityReadScopeGuard.AdmissionScopes);
    }

    private static void Denied(object? result) =>
        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

    private static PaginatedResponse<Activity> Payload(
        ActionResult<PaginatedResponse<Activity>> result) =>
        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<PaginatedResponse<Activity>>().Subject;

    private static void StubPage(Mock<IActivityService> service, IEnumerable<Activity> page) =>
        service.Setup(s => s.GetActivitiesAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

    /// <summary>
    /// Stubs the per-category count with <see cref="CategoryCounts"/>, honouring the contract that
    /// a category the caller did not ask for is not counted — so a total that includes one is a
    /// category the controller asked for and should not have.
    /// </summary>
    private static void StubCounts(Mock<IActivityService> service) =>
        service.Setup(s => s.CountActivitiesByCategoryAsync(
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string> asked, string? _, CancellationToken _) =>
                (IReadOnlyDictionary<string, long>)CategoryCounts
                    .Where(c => asked.Contains(c.Key))
                    .ToDictionary(c => c.Key, c => c.Value));

    private static Activity ActivityWithId(string id) => new() { Id = id, Mills = 1 };

    /// <summary>One record per merged category, each identified by its category's key.</summary>
    private static IEnumerable<Activity> OneRecordPerCategory() =>
        CategoryScopes.Keys.Select(ActivityWithId).ToList();

    /// <summary>Distinct per-category counts, so a total names the categories that produced it.</summary>
    private static readonly Dictionary<string, long> CategoryCounts = new()
    {
        [Scope.HeartRateRead] = 71,
        [Scope.StepCountRead] = 13,
        [Scope.SleepRead] = 5,
        [Scope.TreatmentsRead] = 11,
    };

    private static readonly Dictionary<string, string> CategoryScopes = new()
    {
        ["hr"] = Scope.HeartRateRead,
        ["sc"] = Scope.StepCountRead,
        ["sleep"] = Scope.SleepRead,
        ["regular"] = Scope.TreatmentsRead,
    };

    private static (ActivityController Controller, Mock<IActivityService> Service) BuildRead(
        string[] grantedScopes) =>
        Build(requiredScope: null, grantedScopes, a => CategoryScopes[a.Id!]);

    private static (ActivityController Controller, Mock<IActivityService> Service) Build(
        string? requiredScope,
        string[] grantedScopes,
        Func<Activity, string>? requiredReadScope = null)
    {
        var service = new Mock<IActivityService>(MockBehavior.Strict);
        var decomposer = new Mock<IActivityDecomposer>(MockBehavior.Loose);
        decomposer.Setup(d => d.RequiredWriteScope(It.IsAny<Activity>())).Returns(requiredScope);
        if (requiredReadScope is not null)
            decomposer.Setup(d => d.RequiredReadScope(It.IsAny<Activity>())).Returns(requiredReadScope);

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
