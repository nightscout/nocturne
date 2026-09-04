using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

public class ActogramReportServiceTests
{
    // 2023-11-15T00:00:00Z
    private const long StartMills = 1700000000000L;
    // +14 days
    private const long EndMills = StartMills + 14L * 24 * 60 * 60 * 1000;

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<ISleepService> _sleep = new();
    private readonly Mock<IStepCountService> _steps = new();
    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<ITherapySettingsResolver> _therapy = new();
    private readonly Mock<ITargetRangeResolver> _targetRange = new();

    private ActogramReportService CreateService() =>
        new(
            _glucose.Object,
            _sleep.Object,
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
        result.StepDayTotals.Should().HaveCount(15);
        result.StepDayTotals.Values.Should().AllSatisfy(v => v.Should().Be(0));
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
        var sleepSession = new SleepSession
        {
            StartTime = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 300_000).UtcDateTime,
            EndTime = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 600_000).UtcDateTime,
            Type = SleepSessionType.Overnight,
            Stages = new List<SleepStageInterval>
            {
                new()
                {
                    Stage = SleepStageType.Deep,
                    StartTime = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 300_000).UtcDateTime,
                    EndTime = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 600_000).UtcDateTime,
                },
            },
        };

        _glucose
            .Setup(g => g.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { glucoseRow });
        _sleep
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { sleepSession });
        _steps
            .Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { stepRow });
        _heartRates
            .Setup(h => h.GetHeartRatesByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { hrRow });
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Glucose.Should().ContainSingle().Which.Sgv.Should().Be(142);
        result.HeartRates.Should().ContainSingle().Which.Bpm.Should().Be(72);
        result.StepCounts.Should().ContainSingle().Which.Steps.Should().Be(250);
        result.StepDayTotals["2023-11-14"].Should().Be(250);
        result.SleepSpans.Should().ContainSingle();
        result.SleepSpans[0].State.Should().Be("deep");
        result.SleepSpans[0].StartMills.Should().Be(StartMills + 300_000);
        result.SleepSpans[0].EndMills.Should().Be(StartMills + 600_000);
    }

    [Fact]
    public async Task GetAsync_SleepSessionWithoutStages_FallsBackToAsleepState()
    {
        SetupEmpty();
        _sleep
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new SleepSession
                {
                    StartTime = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 1000).UtcDateTime,
                    EndTime = DateTimeOffset.FromUnixTimeMilliseconds(StartMills + 2000).UtcDateTime,
                    Type = SleepSessionType.Overnight,
                    Stages = null,
                },
            });
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.SleepSpans.Should().ContainSingle();
        result.SleepSpans[0].State.Should().Be("asleep");
        result.SleepSpans[0].StartMills.Should().Be(StartMills + 1000);
        result.SleepSpans[0].EndMills.Should().Be(StartMills + 2000);
    }

    [Fact]
    public async Task GetAsync_SumsStepsByTenantLocalDay_WithEveryDayOfTheWindowPresent()
    {
        SetupEmpty();
        SetupSteps(
            (StartMills + 30 * 60_000, 300),
            (StartMills + 60 * 60_000, 500),
            (StartMills + 90 * 60_000, 42),
            (StartMills - 2L * 24 * 60 * 60_000, 999));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _therapy
            .Setup(t => t.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Europe/Stockholm");

        var result = await CreateService().GetAsync(StartMills, EndMills);

        // The window opens at 22:13:20Z on 14 Nov, 23:13 in Stockholm. The first sample is still
        // 14 Nov there; the other two have crossed local midnight while UTC has not.
        result.StepDayTotals["2023-11-14"].Should().Be(300);
        result.StepDayTotals["2023-11-15"].Should().Be(542);
        // 14 Nov 23:13 to 28 Nov 23:13 local touches fifteen calendar days, the rest empty.
        result.StepDayTotals.Should().HaveCount(15);
        result.StepDayTotals.Should().ContainKey("2023-11-28");
        result.StepDayTotals.Values.Count(v => v == 0).Should().Be(13);
        // A sample outside the window neither counts nor adds a day.
        result.StepDayTotals.Should().NotContainKey("2023-11-12");
    }

    [Fact]
    public async Task GetAsync_StepDayTotals_DoNotIncludeTheDayAnExclusiveEndOpens()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        // 2023-11-16T00:00:00Z exactly: the window ends as that day opens, so it is not touched.
        const long endAtMidnight = 1700092800000L;

        var result = await CreateService().GetAsync(StartMills, endAtMidnight);

        result.StepDayTotals.Keys.Should().BeEquivalentTo("2023-11-14", "2023-11-15");
    }

    [Fact]
    public async Task GetAsync_WithoutATherapyTimezone_SumsStepsByUtcDay()
    {
        SetupEmpty();
        SetupSteps((StartMills + 30 * 60_000, 300), (StartMills + 60 * 60_000, 500));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.StepDayTotals["2023-11-14"].Should().Be(800);
        result.StepDayTotals["2023-11-15"].Should().Be(0);
        result.StepDayTotals.Should().HaveCount(15);
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

    private void SetupSteps(params (long Mills, int Metric)[] samples) =>
        _steps
            .Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(samples
                .Select(s => new StepCount
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime,
                    Metric = s.Metric,
                })
                .ToArray());

    private void SetupEmpty()
    {
        _glucose
            .Setup(g => g.GetAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SensorGlucose>());
        _sleep
            .Setup(s => s.GetSessionsAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<SleepSessionType?>(),
                It.IsAny<SleepSource?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SleepSession>());
        _steps
            .Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StepCount>());
        _heartRates
            .Setup(h => h.GetHeartRatesByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HeartRate>());
    }
}
