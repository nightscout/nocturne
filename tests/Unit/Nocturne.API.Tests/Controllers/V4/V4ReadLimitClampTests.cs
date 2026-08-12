using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Controllers.V4.Devices;
using Nocturne.API.Controllers.V4.Health;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.API.Controllers.V4.Platform;
using Nocturne.API.Controllers.V4.Profiles;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.API.Services.Compatibility;
using Nocturne.API.Services.Monitoring;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4;

/// <summary>
/// Boundary tests for the V4 read routes that declare their own <c>limit</c>/<c>count</c> instead
/// of inheriting one from the shared base controllers, whose own boundaries are covered by
/// <c>V4ReadOnlyControllerBaseTests</c> and <c>V4CrudControllerBaseTests</c>.
/// </summary>
/// <remarks>
/// Grouped rather than split per controller because only four of these controllers have a test
/// class of their own; those four are covered in place, next to their existing fixtures
/// (<c>AuditControllerTests</c>, <c>FoodsControllerTests</c>, <c>NutritionControllerTests</c>,
/// <c>ActivityControllerV4Tests</c>). <c>V4ReadLimitCoverageTests</c> guards that the whole
/// surface stays covered.
/// </remarks>
[Trait("Category", "Unit")]
public class V4ReadLimitClampTests
{
    private const int Ceiling = V4ReadLimits.MaxPageSize;
    private const int AboveCeiling = V4ReadLimits.MaxPageSize + 1;

    // ── State spans ─────────────────────────────────────────────────

    [Fact]
    public async Task StateSpans_LimitAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<IStateSpanService>();
        var controller = new StateSpansController(service.Object);

        await controller.GetStateSpans(limit: Ceiling, offset: 0);

        VerifySpansFetched(service, Ceiling, 0, Times.Once());
    }

    [Fact]
    public async Task StateSpans_LimitAboveCeiling_IsClamped()
    {
        var service = new Mock<IStateSpanService>();
        var controller = new StateSpansController(service.Object);

        await controller.GetStateSpans(limit: AboveCeiling, offset: -1);

        VerifySpansFetched(service, Ceiling, 0, Times.Once());
    }

    [Fact]
    public async Task StateSpansCategorySubRoutes_LimitAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<IStateSpanService>();
        var controller = new StateSpansController(service.Object);

        await InvokeCategorySubRoutes(controller, Ceiling, 0);

        VerifySpansFetched(service, Ceiling, 0, Times.Exactly(CategorySubRouteCount));
    }

    [Fact]
    public async Task StateSpansCategorySubRoutes_LimitAboveCeiling_IsClamped()
    {
        var service = new Mock<IStateSpanService>();
        var controller = new StateSpansController(service.Object);

        await InvokeCategorySubRoutes(controller, AboveCeiling, -1);

        VerifySpansFetched(service, Ceiling, 0, Times.Exactly(CategorySubRouteCount));
    }

    [Fact]
    public async Task StateSpanActivities_LimitAboveCeiling_IsClampedInTheReportedPage()
    {
        var service = new Mock<IStateSpanService>();
        var controller = new StateSpansController(service.Object);

        var result = await controller.GetActivities(limit: AboveCeiling, offset: -1);

        var page = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<PaginatedResponse<StateSpan>>().Subject;
        page.Pagination.Limit.Should().Be(Ceiling);
        page.Pagination.Offset.Should().Be(0);
    }

    /// <summary>The <c>/state-spans</c> sub-routes that pre-filter by a single category.</summary>
    private const int CategorySubRouteCount = 8;

    private static async Task InvokeCategorySubRoutes(StateSpansController controller, int limit, int offset)
    {
        await controller.GetPumpModes(limit: limit, offset: offset);
        await controller.GetConnectivity(limit: limit, offset: offset);
        await controller.GetOverrides(limit: limit, offset: offset);
        await controller.GetTemporaryTargets(limit: limit, offset: offset);
        await controller.GetProfiles(limit: limit, offset: offset);
        await controller.GetExercise(limit: limit, offset: offset);
        await controller.GetIllness(limit: limit, offset: offset);
        await controller.GetTravel(limit: limit, offset: offset);
    }

    private static void VerifySpansFetched(Mock<IStateSpanService> service, int count, int skip, Times times) =>
        service.Verify(s => s.GetStateSpansAsync(
            It.IsAny<StateSpanCategory?>(), null, null, null, null, null,
            count, skip, true, It.IsAny<CancellationToken>()), times);

    // ── Therapy settings ────────────────────────────────────────────

    [Fact]
    public async Task TherapySettings_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        var repo = new Mock<ITherapySettingsRepository>();

        await CreateProfileController(repo).GetTherapySettings(null, null, Ceiling, 0);

        VerifyTherapySettingsFetched(repo, Ceiling, 0);
    }

    [Fact]
    public async Task TherapySettings_LimitAboveCeiling_IsClamped()
    {
        var repo = new Mock<ITherapySettingsRepository>();

        await CreateProfileController(repo).GetTherapySettings(null, null, AboveCeiling, -1);

        VerifyTherapySettingsFetched(repo, Ceiling, 0);
    }

    private static ProfileController CreateProfileController(Mock<ITherapySettingsRepository> repo) =>
        new(
            repo.Object,
            Mock.Of<IBasalScheduleRepository>(),
            Mock.Of<ICarbRatioScheduleRepository>(),
            Mock.Of<ISensitivityScheduleRepository>(),
            Mock.Of<ITargetRangeScheduleRepository>(),
            Mock.Of<IProfileProjectionService>());

    private static void VerifyTherapySettingsFetched(Mock<ITherapySettingsRepository> repo, int limit, int offset) =>
        repo.Verify(r => r.GetAsync(null, null, null, null, limit, offset, true, It.IsAny<CancellationToken>()), Times.Once);

    // ── Sleep sessions ──────────────────────────────────────────────

    [Fact]
    public async Task SleepSessions_LimitAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<ISleepService>();

        await new SleepController(service.Object).GetSessions(limit: Ceiling, offset: 0);

        VerifySessionsFetched(service, Ceiling, 0);
    }

    [Fact]
    public async Task SleepSessions_LimitAboveCeiling_IsClamped()
    {
        var service = new Mock<ISleepService>();

        await new SleepController(service.Object).GetSessions(limit: AboveCeiling, offset: -1);

        VerifySessionsFetched(service, Ceiling, 0);
    }

    private static void VerifySessionsFetched(Mock<ISleepService> service, int limit, int offset) =>
        service.Verify(s => s.GetSessionsAsync(
            null, null, null, null, limit, offset, true, false, It.IsAny<CancellationToken>()), Times.Once);

    // ── Charge cycles ───────────────────────────────────────────────

    [Fact]
    public async Task ChargeCycles_LimitAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<IBatteryService>();
        var controller = new BatteryController(service.Object, Mock.Of<ILogger<BatteryController>>());

        var result = await controller.GetChargeCycles(limit: Ceiling);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyChargeCyclesFetched(service, Ceiling);
    }

    [Fact]
    public async Task ChargeCycles_LimitAboveCeiling_IsClamped()
    {
        var service = new Mock<IBatteryService>();
        var controller = new BatteryController(service.Object, Mock.Of<ILogger<BatteryController>>());

        var result = await controller.GetChargeCycles(limit: AboveCeiling);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyChargeCyclesFetched(service, Ceiling);
    }

    private static void VerifyChargeCyclesFetched(Mock<IBatteryService> service, int limit) =>
        service.Verify(s => s.GetChargeCyclesAsync(null, null, null, limit, It.IsAny<CancellationToken>()), Times.Once);

    // ── Body weight ─────────────────────────────────────────────────

    [Fact]
    public async Task BodyWeights_CountAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<IBodyWeightService>();
        var controller = new BodyWeightController(service.Object, Mock.Of<ILogger<BodyWeightController>>());

        var result = await controller.GetBodyWeights(count: Ceiling, skip: 0);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.GetBodyWeightsAsync(Ceiling, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BodyWeights_CountAboveCeiling_IsClamped()
    {
        var service = new Mock<IBodyWeightService>();
        var controller = new BodyWeightController(service.Object, Mock.Of<ILogger<BodyWeightController>>());

        var result = await controller.GetBodyWeights(count: AboveCeiling, skip: -1);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.GetBodyWeightsAsync(Ceiling, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Heart rate ──────────────────────────────────────────────────

    [Fact]
    public async Task HeartRates_CountAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<IHeartRateService>();
        var controller = new HeartRateController(service.Object, Mock.Of<ILogger<HeartRateController>>());

        var result = await controller.GetHeartRates(count: Ceiling, skip: 0);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.GetHeartRatesAsync(Ceiling, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HeartRates_CountAboveCeiling_IsClamped()
    {
        var service = new Mock<IHeartRateService>();
        var controller = new HeartRateController(service.Object, Mock.Of<ILogger<HeartRateController>>());

        var result = await controller.GetHeartRates(count: AboveCeiling, skip: -1);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.GetHeartRatesAsync(Ceiling, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Step count ──────────────────────────────────────────────────

    [Fact]
    public async Task StepCounts_CountAtCeiling_ReachesServiceUnchanged()
    {
        var service = new Mock<IStepCountService>();
        var controller = new StepCountController(service.Object, Mock.Of<ILogger<StepCountController>>());

        var result = await controller.GetStepCounts(count: Ceiling, skip: 0);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.GetStepCountsAsync(Ceiling, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StepCounts_CountAboveCeiling_IsClamped()
    {
        var service = new Mock<IStepCountService>();
        var controller = new StepCountController(service.Object, Mock.Of<ILogger<StepCountController>>());

        var result = await controller.GetStepCounts(count: AboveCeiling, skip: -1);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.GetStepCountsAsync(Ceiling, 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Compatibility analyses ──────────────────────────────────────

    [Fact]
    public async Task CompatibilityAnalyses_CountAtCeiling_ReachesRepositoryUnchanged()
    {
        var repo = new Mock<IDiscrepancyAnalysisRepository>();

        var result = await CreateCompatibilityController(repo).GetAnalyses(count: Ceiling, skip: 0);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyAnalysesFetched(repo, Ceiling, 0);
    }

    [Fact]
    public async Task CompatibilityAnalyses_CountAboveCeiling_IsClamped()
    {
        var repo = new Mock<IDiscrepancyAnalysisRepository>();

        var result = await CreateCompatibilityController(repo).GetAnalyses(count: AboveCeiling, skip: -1);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyAnalysesFetched(repo, Ceiling, 0);
    }

    private static CompatibilityController CreateCompatibilityController(Mock<IDiscrepancyAnalysisRepository> repo) =>
        new(
            Mock.Of<IDiscrepancyPersistenceService>(),
            repo.Object,
            Options.Create(new CompatibilityProxyConfiguration()),
            Mock.Of<ILogger<CompatibilityController>>());

    // ── Discrepancy analyses ────────────────────────────────────────

    [Fact]
    public async Task DiscrepancyAnalyses_CountAtCeiling_ReachesRepositoryUnchanged()
    {
        var repo = new Mock<IDiscrepancyAnalysisRepository>();
        var controller = new DiscrepancyController(repo.Object, Mock.Of<ILogger<DiscrepancyController>>());

        var result = await controller.GetDiscrepancyAnalyses(count: Ceiling, skip: 0);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyAnalysesFetched(repo, Ceiling, 0);
    }

    [Fact]
    public async Task DiscrepancyAnalyses_CountAboveCeiling_IsClamped()
    {
        var repo = new Mock<IDiscrepancyAnalysisRepository>();
        var controller = new DiscrepancyController(repo.Object, Mock.Of<ILogger<DiscrepancyController>>());

        var result = await controller.GetDiscrepancyAnalyses(count: AboveCeiling, skip: 0);

        result.Result.Should().BeOfType<OkObjectResult>();
        VerifyAnalysesFetched(repo, Ceiling, 0);
    }

    private static void VerifyAnalysesFetched(Mock<IDiscrepancyAnalysisRepository> repo, int count, int skip) =>
        repo.Verify(r => r.GetAnalysesAsync(null, null, null, null, count, skip, It.IsAny<CancellationToken>()), Times.Once);

    // ── System events ───────────────────────────────────────────────

    [Fact]
    public async Task SystemEvents_CountAtCeiling_ReachesRepositoryUnchanged()
    {
        var repo = new Mock<ISystemEventRepository>();

        await new SystemEventsController(repo.Object).GetSystemEvents(count: Ceiling, skip: 0);

        VerifySystemEventsFetched(repo, Ceiling, 0);
    }

    [Fact]
    public async Task SystemEvents_CountAboveCeiling_IsClamped()
    {
        var repo = new Mock<ISystemEventRepository>();

        await new SystemEventsController(repo.Object).GetSystemEvents(count: AboveCeiling, skip: -1);

        VerifySystemEventsFetched(repo, Ceiling, 0);
    }

    private static void VerifySystemEventsFetched(Mock<ISystemEventRepository> repo, int count, int skip) =>
        repo.Verify(r => r.GetSystemEventsAsync(
            null, null, null, null, null, count, skip, It.IsAny<CancellationToken>()), Times.Once);

    // ── Tracker instance history ────────────────────────────────────

    [Fact]
    public async Task TrackerHistory_LimitAtCeiling_ReachesRepositoryUnchanged()
    {
        var repo = new Mock<ITrackerRepository>();

        await CreateTrackersController(repo).GetInstanceHistory(Ceiling);

        repo.Verify(r => r.GetCompletedInstancesAsync(null, Ceiling, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TrackerHistory_LimitAboveCeiling_IsClamped()
    {
        var repo = new Mock<ITrackerRepository>();

        await CreateTrackersController(repo).GetInstanceHistory(AboveCeiling);

        repo.Verify(r => r.GetCompletedInstancesAsync(null, Ceiling, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static TrackersController CreateTrackersController(Mock<ITrackerRepository> repo) =>
        new(
            repo.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<ITrackerAlertRuleSyncService>(),
            Mock.Of<ITenantDbContextFactory>(),
            Mock.Of<IAlertAcknowledgementService>(),
            Mock.Of<ILogger<TrackersController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
