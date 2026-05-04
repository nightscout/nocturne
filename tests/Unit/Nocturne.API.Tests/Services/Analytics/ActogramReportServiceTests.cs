using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

public class ActogramReportServiceTests
{
    // 2023-11-15T00:00:00Z
    private const long StartMills = 1700000000000L;
    // +14 days
    private const long EndMills = StartMills + 14L * 24 * 60 * 60 * 1000;

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<IStateSpanService> _stateSpans = new();
    private readonly Mock<IStepCountService> _steps = new();
    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<ITherapySettingsResolver> _therapy = new();
    private readonly Mock<ITargetRangeResolver> _targetRange = new();

    private ActogramReportService CreateService() =>
        new(
            _glucose.Object,
            _stateSpans.Object,
            _steps.Object,
            _heartRates.Object,
            _therapy.Object,
            _targetRange.Object,
            NullLogger<ActogramReportService>.Instance
        );

    [Fact]
    public async Task GetAsync_NoData_ReturnsEmptyResultWithDefaultThresholds()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Glucose.Should().BeEmpty();
        result.HeartRates.Should().BeEmpty();
        result.StepCounts.Should().BeEmpty();
        result.SleepSpans.Should().BeEmpty();
        result.Thresholds.VeryLow.Should().Be(54);
        result.Thresholds.Low.Should().Be(70);
        result.Thresholds.High.Should().Be(180);
        result.Thresholds.VeryHigh.Should().Be(250);
        result.Thresholds.GlucoseYMax.Should().Be(300);
    }

    [Fact]
    public async Task GetAsync_WithProfile_ResolvesThresholdsFromProfile()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _targetRange
            .Setup(t => t.GetLowBGTargetAsync(EndMills, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(80.0);
        _targetRange
            .Setup(t => t.GetHighBGTargetAsync(EndMills, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(160.0);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Thresholds.Low.Should().Be(80.0);
        result.Thresholds.High.Should().Be(160.0);
        result.Thresholds.VeryLow.Should().Be(54);
        result.Thresholds.VeryHigh.Should().Be(250);
    }

    [Fact]
    public async Task GetAsync_MapsAllSeriesToLeanDtos()
    {
        var glucoseRow = new SensorGlucose
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 60_000).UtcDateTime,
            Mgdl = 142,
        };
        var hrRow = new HeartRate
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 120_000).UtcDateTime,
            Bpm = 72,
        };
        var stepRow = new StepCount
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 180_000).UtcDateTime,
            Metric = 250,
        };
        var sleepRow = new StateSpan
        {
            Category = StateSpanCategory.Sleep,
            State = "Deep",
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 300_000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 600_000).UtcDateTime,
        };

        _glucose
            .Setup(g => g.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { glucoseRow });
        _stateSpans
            .Setup(s => s.GetStateSpansAsync(StateSpanCategory.Sleep, null,
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sleepRow });
        _steps
            .Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stepRow });
        _heartRates
            .Setup(h => h.GetHeartRatesByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { hrRow });
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Glucose.Should().ContainSingle().Which.Sgv.Should().Be(142);
        result.HeartRates.Should().ContainSingle().Which.Bpm.Should().Be(72);
        result.StepCounts.Should().ContainSingle().Which.Steps.Should().Be(250);
        result.SleepSpans.Should().ContainSingle();
        result.SleepSpans[0].State.Should().Be("Deep");
        result.SleepSpans[0].StartMills.Should().Be(StartMills + 300_000);
        result.SleepSpans[0].EndMills.Should().Be(StartMills + 600_000);
    }

    [Fact]
    public async Task GetAsync_OpenSleepSpan_FallsBackToStartMillsForEnd()
    {
        SetupEmpty();
        _stateSpans
            .Setup(s => s.GetStateSpansAsync(StateSpanCategory.Sleep, null,
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new StateSpan
                {
                    Category = StateSpanCategory.Sleep,
                    State = "Light",
                    StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 1000).UtcDateTime,
                    EndTimestamp = null,
                },
            });
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.SleepSpans.Should().ContainSingle();
        result.SleepSpans[0].EndMills.Should().Be(result.SleepSpans[0].StartMills);
    }

    [Fact]
    public async Task GetAsync_DoesNotInvokeBasalRateResolver()
    {
        // Guard rail: the actogram must not pull anything from the IOB/COB/basal pipeline.
        // No IBasalRateResolver mock is wired here — a regression that introduces it would
        // surface as a missing dependency at construction time.
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _targetRange.Setup(t => t.GetLowBGTargetAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(80);
        _targetRange.Setup(t => t.GetHighBGTargetAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(160);

        var act = async () => await CreateService().GetAsync(StartMills, EndMills);

        await act.Should().NotThrowAsync();
    }

    private void SetupEmpty()
    {
        _glucose
            .Setup(g => g.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());
        _stateSpans
            .Setup(s => s.GetStateSpansAsync(StateSpanCategory.Sleep, null,
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StateSpan>());
        _steps
            .Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StepCount>());
        _heartRates
            .Setup(h => h.GetHeartRatesByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HeartRate>());
    }
}
