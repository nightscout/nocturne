using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.ChartData.Stages;

public class IobCobComputeStageTests
{
    private readonly Mock<IIobService> _mockIobService = new();
    private readonly Mock<ICobService> _mockCobService = new();
    private readonly Mock<ITherapySettingsResolver> _mockTherapySettings = new();
    private readonly Mock<IBasalRateResolver> _mockBasalRateResolver = new();
    private readonly Mock<ITherapyTimelineResolver> _mockTherapyTimeline = new();
    private readonly Mock<IApsSnapshotRepository> _mockApsRepo = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IobCobComputeStage _stage;

    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    private static TherapySnapshot DefaultSnapshot() => new(
        dia: 3.0,
        peakMinutes: 75,
        carbsPerHour: 30.0,
        timezone: null,
        ccpPercentage: null,
        ccpTimeshiftMs: 0,
        sensitivityEntries: null,
        carbRatioEntries: null,
        basalEntries: null
    );

    public IobCobComputeStageTests()
    {
        _mockTherapySettings.Setup(p => p.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        _mockTherapyTimeline
            .Setup(t => t.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline(new[] { new TherapySegment(from, to, DefaultSnapshot()) })
            );

        _mockApsRepo
            .Setup(r => r.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<ApsSnapshot>());

        _stage = new IobCobComputeStage(
            _mockIobService.Object,
            _mockCobService.Object,
            _mockTherapySettings.Object,
            _mockBasalRateResolver.Object,
            _mockTherapyTimeline.Object,
            _mockApsRepo.Object,
            _cache,
            MockTenantAccessor.Create().Object,
            NullLogger<IobCobComputeStage>.Instance
        );
    }

    [Fact]
    public async Task ExecuteAsync_ComputesIobCobAndBasalSeries()
    {
        // Arrange
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000; // 30 minutes later
        const int intervalMinutes = 5;

        var bolus = new Treatment
        {
            Mills = TestMills - 60 * 60 * 1000, // 1 hour before start
            Insulin = 3.0,
        };

        var carbIntake = new Treatment
        {
            Mills = TestMills - 30 * 60 * 1000, // 30 minutes before start
            Carbs = 45.0,
        };

        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + 30 * 60 * 1000).UtcDateTime,
            Rate = 1.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        _mockIobService
            .Setup(s => s.FromTreatments(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>()))
            .Returns(new IobResult { Iob = 2.0 });

        _mockIobService
            .Setup(s => s.FromTempBasals(It.IsAny<IReadOnlyList<TempBasal>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>()))
            .Returns(new IobResult { BasalIob = 0.5 });

        _mockCobService
            .Setup(s => s.CobTotal(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>(), It.IsAny<DeviceCobSnapshot?>(), It.IsAny<long>()))
            .Returns(new CobResult { Cob = 20.0 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            SyntheticTreatments = [bolus, carbIntake],
            TempBasalList = [tempBasal],
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IobSeries.Should().NotBeEmpty();
        result.CobSeries.Should().NotBeEmpty();
        result.BasalSeries.Should().NotBeEmpty();
        result.MaxIob.Should().BeGreaterThanOrEqualTo(3); // floored at 3
        result.MaxCob.Should().BeGreaterThanOrEqualTo(30); // floored at 30
        result.MaxBasalRate.Should().BeGreaterThan(0);

        // Verify series timestamps are within expected range
        result.IobSeries.Should().AllSatisfy(p => p.Timestamp.Should().BeInRange(startTime, endTime));
        result.CobSeries.Should().AllSatisfy(p => p.Timestamp.Should().BeInRange(startTime, endTime));
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyData_ReturnsEmptySeries()
    {
        // Arrange
        var startTime = TestMills;
        var endTime = TestMills; // zero-length window produces only a single point
        const int intervalMinutes = 5;
        const double defaultBasalRate = 1.0;

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = defaultBasalRate,
            SyntheticTreatments = [],
            TempBasalList = [],
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert — IOB/COB services should never be called with no treatments
        _mockIobService.Verify(
            s => s.FromTreatments(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>()),
            Times.Never
        );
        _mockCobService.Verify(
            s => s.CobTotal(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>(), It.IsAny<DeviceCobSnapshot?>(), It.IsAny<long>()),
            Times.Never
        );

        // Floors still apply even with empty data
        result.MaxIob.Should().Be(3);
        result.MaxCob.Should().Be(30);

        // Basal series falls back to profile-based (produces at least one point)
        result.BasalSeries.Should().NotBeEmpty();
        result.BasalSeries.Should().AllSatisfy(b => b.Rate.Should().Be(defaultBasalRate));

        // IobSeries and CobSeries have exactly one point (start == end)
        result.IobSeries.Should().ContainSingle();
        result.CobSeries.Should().ContainSingle();
        result.IobSeries[0].Value.Should().Be(0);
        result.CobSeries[0].Value.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_OnlyPassesActiveWindowToInsulinAndCarbServices()
    {
        // Arrange — DIA = 3h (default when HasDataAsync returns false), COB window = 6h
        var startTime = TestMills;
        var endTime = TestMills + 60 * 60 * 1000; // 1 hour window
        const int intervalMinutes = 5;
        const long oneHour = 60 * 60 * 1000;

        // Insulin: one well outside DIA, one inside
        var staleInsulin = new Treatment { Mills = TestMills - 10 * oneHour, Insulin = 1.0 };
        var freshInsulin = new Treatment { Mills = TestMills - 30 * 60 * 1000, Insulin = 2.0 };
        // Carbs: one well outside COB absorption, one inside
        var staleCarbs = new Treatment { Mills = TestMills - 12 * oneHour, Carbs = 30.0 };
        var freshCarbs = new Treatment { Mills = TestMills - 30 * 60 * 1000, Carbs = 45.0 };
        // Temp basals: one outside DIA, one inside
        var staleTempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 10 * oneHour).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 9 * oneHour).UtcDateTime,
            Rate = 1.0,
            Origin = TempBasalOrigin.Algorithm,
        };
        var freshTempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 30 * 60 * 1000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills + 30 * 60 * 1000).UtcDateTime,
            Rate = 1.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        var insulinCalls = new List<List<Treatment>>();
        var carbCalls = new List<List<Treatment>>();
        var tempBasalCalls = new List<List<TempBasal>>();

        _mockIobService
            .Setup(s => s.FromTreatments(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>()))
            .Callback<IReadOnlyList<Treatment>, long, TherapySnapshot>((list, _, _) => insulinCalls.Add(new List<Treatment>(list)))
            .Returns(new IobResult { Iob = 0 });
        _mockIobService
            .Setup(s => s.FromTempBasals(It.IsAny<IReadOnlyList<TempBasal>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>()))
            .Callback<IReadOnlyList<TempBasal>, long, TherapySnapshot>((list, _, _) => tempBasalCalls.Add(new List<TempBasal>(list)))
            .Returns(new IobResult { BasalIob = 0 });
        _mockCobService
            .Setup(s => s.CobTotal(It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<long>(), It.IsAny<TherapySnapshot>(), It.IsAny<DeviceCobSnapshot?>(), It.IsAny<long>()))
            .Callback<IReadOnlyList<Treatment>, long, TherapySnapshot, DeviceCobSnapshot?, long>((list, _, _, _, _) => carbCalls.Add(new List<Treatment>(list)))
            .Returns(new CobResult { Cob = 0 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            SyntheticTreatments = [staleInsulin, freshInsulin, staleCarbs, freshCarbs],
            TempBasalList = [staleTempBasal, freshTempBasal],
        };

        // Act
        await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert — every call to the per-tick services receives only the in-window entries
        insulinCalls.Should().NotBeEmpty();
        insulinCalls.Should().AllSatisfy(call =>
        {
            call.Should().NotContain(staleInsulin);
            call.Should().Contain(freshInsulin);
        });
        carbCalls.Should().NotBeEmpty();
        carbCalls.Should().AllSatisfy(call =>
        {
            call.Should().NotContain(staleCarbs);
            call.Should().Contain(freshCarbs);
        });
        tempBasalCalls.Should().NotBeEmpty();
        tempBasalCalls.Should().AllSatisfy(call =>
        {
            call.Should().NotContain(staleTempBasal);
            call.Should().Contain(freshTempBasal);
        });
    }
}
