using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Helpers;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Effects;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Boundary tests for the server-side ceilings on the legacy v1 <c>count</c> query parameter.
/// Each read is exercised at exactly its own ceiling (must pass through unchanged) and at one
/// above it (must be clamped), so the assertions pin the edge rather than a value somewhere
/// inside the window.
/// </summary>
/// <remarks>
/// <c>times/</c> and <c>slice/</c> are deliberately absent: <c>TimeQueryService</c> fetches a
/// fixed 1,000 rows per storage, so those routes cannot exceed that regardless of <c>count</c>
/// and carry no ceiling of their own to test.
/// </remarks>
[Trait("Category", "Unit")]
public class LegacyReadCountCapTests
{
    private const int AtCap = LegacyReadLimits.MaxCount;
    private const int AboveCap = LegacyReadLimits.MaxCount + 1;
    private const int AtMergedCap = LegacyReadLimits.MaxMergedCount;
    private const int AboveMergedCap = LegacyReadLimits.MaxMergedCount + 1;

    /// <summary>
    /// The compat floor the ceilings are chosen against: the migration job pages at 10,000 and the
    /// Nightscout connector's page size is attribute-capped at 10,000, so neither ceiling may drop
    /// below that without breaking a first-party bulk caller. Without this, lowering a ceiling to
    /// an arbitrarily small value leaves every boundary case below still green.
    /// </summary>
    [Fact]
    public void Ceilings_StayAboveTheLargestFirstPartyPageSize()
    {
        LegacyReadLimits.MaxCount.Should().BeGreaterThanOrEqualTo(10_000);
        LegacyReadLimits.MaxMergedCount.Should().BeGreaterThanOrEqualTo(10_000);
        LegacyReadLimits.MaxCount.Should().BeGreaterThanOrEqualTo(LegacyReadLimits.MaxMergedCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(AtCap - 1, AtCap - 1)]
    [InlineData(AtCap, AtCap)]
    [InlineData(AboveCap, AtCap)]
    [InlineData(int.MaxValue, AtCap)]
    public void ClampCount_CapsAtCeilingAndLeavesEverythingBelowUntouched(
        int requested,
        int expected
    )
    {
        LegacyReadLimits.ClampCount(requested).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(AtMergedCap - 1, AtMergedCap - 1)]
    [InlineData(AtMergedCap, AtMergedCap)]
    [InlineData(AboveMergedCap, AtMergedCap)]
    [InlineData(int.MaxValue, AtMergedCap)]
    public void ClampMergedCount_CapsAtCeilingAndLeavesEverythingBelowUntouched(
        int requested,
        int expected
    )
    {
        LegacyReadLimits.ClampMergedCount(requested).Should().Be(expected);
    }

    [Theory]
    [InlineData(AtCap, AtCap)]
    [InlineData(AboveCap, AtCap)]
    public async Task Entries_ClampsCountPassedToTheEntryService(int requested, int expected)
    {
        var entryService = new Mock<IEntryService>();
        int? observedCount = null;
        entryService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    string? _,
                    int count,
                    int _,
                    string? _,
                    string? _,
                    bool _,
                    CancellationToken _
                ) => observedCount = count
            )
            .ReturnsAsync(Array.Empty<Entry>());

        var controller = new EntriesController(
            entryService.Object,
            Mock.Of<IDocumentProcessingService>(),
            Mock.Of<IProcessingStatusService>(),
            Mock.Of<ICanonicalAlertEvaluator>(),
            Mock.Of<ILogger<EntriesController>>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.GetEntries(count: requested);

        observedCount.Should().Be(expected);
    }

    [Theory]
    [InlineData(AtCap, AtCap)]
    [InlineData(AboveCap, AtCap)]
    public async Task Treatments_ClampsCountPassedToTheTreatmentService(
        int requested,
        int expected
    )
    {
        var treatmentService = new Mock<ITreatmentService>();
        int? observedCount = null;
        treatmentService
            .Setup(x =>
                x.GetTreatmentsAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (string? _, int? count, int? _, CancellationToken _) => observedCount = count
            )
            .ReturnsAsync(Array.Empty<Treatment>());

        var controller = new TreatmentsController(
            treatmentService.Object,
            Mock.Of<IDocumentProcessingService>(),
            Mock.Of<ILogger<TreatmentsController>>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.GetTreatments(count: requested);

        observedCount.Should().Be(expected);
    }

    [Theory]
    [InlineData(AtMergedCap, AtMergedCap)]
    [InlineData(AboveMergedCap, AtMergedCap)]
    public async Task Activity_ClampsCountPassedToTheActivityService(int requested, int expected)
    {
        var activityService = new Mock<IActivityService>();
        int? observedCount = null;
        activityService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (string? _, int? count, int? _, CancellationToken _) => observedCount = count
            )
            .ReturnsAsync(Array.Empty<Activity>());

        var controller = NewActivityController(activityService.Object);

        await controller.GetActivities(count: requested);

        observedCount.Should().Be(expected);
    }

    /// <summary>
    /// Zero and negative counts answer an empty array without reaching the service, matching the
    /// Nightscout behaviour treatments and devicestatus already implement. Before this handling a
    /// negative count reached <c>Take(-1)</c>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Activity_ReturnsEmptyWithoutReadingForANonPositiveCount(int requested)
    {
        var activityService = new Mock<IActivityService>(MockBehavior.Strict);
        var controller = NewActivityController(activityService.Object);

        var result = await controller.GetActivities(count: requested);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeAssignableTo<IEnumerable<Activity>>()
            .Subject.Should().BeEmpty();
        activityService.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Activity merges its sources in memory and over-fetches <c>count + skip</c> from each, so the
    /// ceiling has to bound the window rather than the page: <c>skip</c> may reach the last record
    /// inside the window, and one past it reads nothing at all.
    /// </summary>
    [Theory]
    [InlineData(AtMergedCap - 1, 1)]
    [InlineData(AtMergedCap - 100, 100)]
    [InlineData(0, AtMergedCap)]
    public async Task Activity_ClampsThePageToWhatRemainsOfThePagingWindow(
        int skip,
        int expected
    )
    {
        var activityService = new Mock<IActivityService>();
        int? observedCount = null;
        activityService
            .Setup(x =>
                x.GetActivitiesAsync(
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (string? _, int? count, int? _, CancellationToken _) => observedCount = count
            )
            .ReturnsAsync(Array.Empty<Activity>());

        var controller = NewActivityController(activityService.Object);

        await controller.GetActivities(count: AtMergedCap, skip: skip);

        observedCount.Should().Be(expected);
    }

    [Theory]
    [InlineData(AtMergedCap)]
    [InlineData(AtMergedCap + 1)]
    [InlineData(int.MaxValue)]
    public async Task Activity_ReadsNothingForAPageStartingPastThePagingWindow(int skip)
    {
        var activityService = new Mock<IActivityService>(MockBehavior.Strict);
        var controller = NewActivityController(activityService.Object);

        var result = await controller.GetActivities(count: AtMergedCap, skip: skip);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeAssignableTo<IEnumerable<Activity>>()
            .Subject.Should().BeEmpty();
        activityService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AtMergedCap, AtMergedCap)]
    [InlineData(AboveMergedCap, AtMergedCap)]
    public async Task DeviceStatus_ClampsCountPassedToTheSnapshotRepository(
        int requested,
        int expected
    )
    {
        var apsRepo = new Mock<IApsSnapshotRepository>();
        int? observedLimit = null;
        apsRepo
            .Setup(x =>
                x.GetAsync(
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    DateTime? _,
                    DateTime? _,
                    string? _,
                    string? _,
                    int limit,
                    int _,
                    bool _,
                    CancellationToken _
                ) => observedLimit = limit
            )
            .ReturnsAsync(Array.Empty<ApsSnapshot>());

        var pumpRepo = new Mock<IPumpSnapshotRepository>();
        pumpRepo
            .Setup(x =>
                x.GetAsync(
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Array.Empty<PumpSnapshot>());

        var projection = new DeviceStatusProjectionService(
            apsRepo.Object,
            pumpRepo.Object,
            Mock.Of<IUploaderSnapshotRepository>(),
            Mock.Of<IStateSpanRepository>(),
            Mock.Of<IDeviceStatusExtrasRepository>(),
            Mock.Of<ILogger<DeviceStatusProjectionService>>()
        );

        var controller = new DeviceStatusController(
            projection,
            Mock.Of<IDeviceStatusDecomposer>(),
            Mock.Of<IWriteSideEffects>(),
            Mock.Of<IDataEventSink<DeviceStatus>>(),
            Mock.Of<ILogger<DeviceStatusController>>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        await controller.GetDeviceStatus(count: requested);

        observedLimit.Should().Be(expected);
    }

    private static ActivityController NewActivityController(IActivityService activityService)
    {
        var controller = new ActivityController(
            activityService,
            Mock.Of<IActivityDecomposer>(),
            Mock.Of<ILogger<ActivityController>>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["GrantedScopes"] = (IReadOnlySet<string>)
            new HashSet<string> { Scope.TreatmentsRead };
        return controller;
    }
}
