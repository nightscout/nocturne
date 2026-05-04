using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.ChartData.Stages;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Mocks;
using Xunit;

namespace Nocturne.API.Tests.Services.ChartData.Stages;

public class IobCobComputeStageTests
{
    private readonly Mock<IIobCalculator> _mockIobCalculator = new();
    private readonly Mock<ICobCalculator> _mockCobCalculator = new();
    private readonly Mock<ITherapySettingsResolver> _mockTherapySettings = new();
    private readonly Mock<IBasalRateResolver> _mockBasalRateResolver = new();
    private readonly Mock<ITherapyTimelineResolver> _mockTherapyTimelineResolver = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly IobCobComputeStage _stage;

    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    public IobCobComputeStageTests()
    {
        _mockTherapySettings.Setup(p => p.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var defaultSnapshot = new TherapySnapshot(
            dia: 3.0,
            peakMinutes: TherapySnapshot.DefaultPeakMinutes,
            carbsPerHour: TherapySnapshot.DefaultCarbsPerHour,
            timezone: null,
            ccpPercentage: null,
            ccpTimeshiftMs: 0,
            sensitivityEntries: null,
            carbRatioEntries: null,
            basalEntries: null);
        _mockTherapyTimelineResolver
            .Setup(r => r.BuildAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long from, long to, string? _, CancellationToken _) =>
                new TherapyTimeline(new[] { new TherapySegment(from, to, defaultSnapshot) }));

        _stage = new IobCobComputeStage(
            _mockIobCalculator.Object,
            _mockCobCalculator.Object,
            _mockTherapySettings.Object,
            _mockBasalRateResolver.Object,
            _mockTherapyTimelineResolver.Object,
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

        var bolus = new Bolus
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 60 * 60 * 1000).UtcDateTime, // 1 hour before start
            Insulin = 3.0,
        };

        var carbIntake = new CarbIntake
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(TestMills - 30 * 60 * 1000).UtcDateTime, // 30 minutes before start
            Carbs = 45.0,
        };

        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + 30 * 60 * 1000).UtcDateTime,
            Rate = 1.5,
            Origin = TempBasalOrigin.Algorithm,
        };

        _mockIobCalculator
            .Setup(s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<long?>()))
            .Returns(new IobResult { Iob = 2.0 });

        _mockIobCalculator
            .Setup(s => s.FromTempBasals(It.IsAny<List<TempBasal>>(), It.IsAny<long?>()))
            .Returns(new IobResult { BasalIob = 0.5 });

        _mockCobCalculator
            .Setup(s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long?>()))
            .Returns(new CobResult { Cob = 20.0 });

        var context = new ChartDataContext
        {
            StartTime = startTime,
            EndTime = endTime,
            IntervalMinutes = intervalMinutes,
            DefaultBasalRate = 1.0,
            BolusList = [bolus],
            CarbIntakeList = [carbIntake],
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
            BolusList = [],
            CarbIntakeList = [],
            TempBasalList = [],
        };

        // Act
        var result = await _stage.ExecuteAsync(context, CancellationToken.None);

        // Assert — IOB/COB calculators should never be called with no data
        _mockIobCalculator.Verify(
            s => s.FromBoluses(It.IsAny<List<Bolus>>(), It.IsAny<long?>()),
            Times.Never
        );
        _mockCobCalculator.Verify(
            s => s.FromCarbIntakes(It.IsAny<List<CarbIntake>>(), It.IsAny<List<Bolus>?>(), It.IsAny<List<TempBasal>?>(), It.IsAny<long?>()),
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
}
