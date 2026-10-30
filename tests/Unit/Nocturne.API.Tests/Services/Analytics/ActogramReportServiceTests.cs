using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Tests.Services.Analytics;

public class ActogramReportServiceTests
{
    // 2023-11-15T00:00:00Z
    private const long StartMills = 1700000000000L;
    // +14 days
    private const long EndMills = StartMills + 14L * 24 * 60 * 60 * 1000;

    // 22:30:00Z on 14 Nov, one hour long
    private const long SummaryStart = StartMills + 1_000_000L;
    private const long SummaryEnd = SummaryStart + 3_600_000L;

    private readonly Mock<ISensorGlucoseRepository> _glucose = new();
    private readonly Mock<ISleepService> _sleep = new();
    private readonly Mock<IStepCountService> _steps = new();
    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<ITherapySettingsResolver> _therapy = new();
    private readonly Mock<ITargetRangeResolver> _targetRange = new();

    private readonly CategoryReadContext _readContext = new();

    private ActogramReportService CreateService() =>
        new(
            _glucose.Object,
            _sleep.Object,
            _steps.Object,
            _heartRates.Object,
            _therapy.Object,
            _targetRange.Object,
            new TenantClockResolver(_therapy.Object, _readContext, NullLogger<TenantClockResolver>.Instance),
            NullLogger<ActogramReportService>.Instance
        );

    [Fact]
    public async Task GetAsync_NoData_ReturnsEmptyResultWithDefaultThresholds()
    {
        SetupEmpty();
        SetupZone("UTC");
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
        result.Thresholds.TargetLow.Should().BeNull();
        result.Thresholds.TargetHigh.Should().BeNull();
        result.Thresholds.GlucoseYMax.Should().Be(300);
    }

    [Fact]
    public async Task GetAsync_WithProfile_KeepsClinicalBandsAndCarriesProfileTarget()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _targetRange
            .Setup(t => t.GetLowBGTargetAsync(EndMills, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(95.0);
        _targetRange
            .Setup(t => t.GetHighBGTargetAsync(EndMills, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(115.0);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Thresholds.Low.Should().Be(70);
        result.Thresholds.High.Should().Be(180);
        result.Thresholds.VeryLow.Should().Be(54);
        result.Thresholds.VeryHigh.Should().Be(250);
        result.Thresholds.TargetLow.Should().Be(95.0);
        result.Thresholds.TargetHigh.Should().Be(115.0);
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
            .Setup(g => g.GetForAnalyticsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
            .Setup(h => h.GetHeartRateMinuteAveragesByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { hrRow });
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("UTC");

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
        SetupZone("Europe/Stockholm");

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
        SetupZone("UTC");

        var result = await CreateService().GetAsync(StartMills, endAtMidnight);

        result.StepDayTotals.Keys.Should().BeEquivalentTo("2023-11-14", "2023-11-15");
    }

    [Theory]
    [InlineData(null, TimeZoneUnavailableReason.NotConfigured)]
    [InlineData("", TimeZoneUnavailableReason.NotConfigured)]
    [InlineData("Not/AZone", TimeZoneUnavailableReason.Unrecognised)]
    public async Task GetAsync_WithoutAUsableTimezone_ServesUtcDaysAndSaysWhy(
        string? stored,
        TimeZoneUnavailableReason expected)
    {
        SetupEmpty();
        SetupSteps((StartMills + 30 * 60_000, 300), (StartMills + 60 * 60_000, 500));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone(stored);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Days.Select(d => d.Date).Should().StartWith("2023-11-14").And.HaveCount(15);
        result.Days[0].EndMills.Should().Be(result.Days[1].StartMills);
        result.Days[1].StartMills.Should().Be(1700006400000L);
        result.StepDayTotals["2023-11-14"].Should().Be(800);
        result.TimeZoneUnavailableReason.Should().Be(expected);
        result.StepCounts.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_WithATimezone_CarriesNoReason()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("Australia/Brisbane");

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.TimeZoneUnavailableReason.Should().BeNull();
        result.StepDayTotals.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetAsync_ForAShare_ServesUtcDaysAndSaysShare_EvenWhenAZoneIsCached()
    {
        SetupEmpty();
        SetupSteps((StartMills + 30 * 60_000, 300));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("Australia/Brisbane");
        _readContext.MarkShare();

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Days[0].Date.Should().Be("2023-11-14");
        result.Days[0].StartMills.Should().Be(1699920000000L);
        result.TimeZoneUnavailableReason.Should().Be(TimeZoneUnavailableReason.Share);
    }

    [Fact]
    public async Task GetAsync_ServesEachTenantLocalDayWithItsStartAndEndInstants()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("Europe/Stockholm");

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.Days.Should().HaveCount(15);
        result.Days[0].Date.Should().Be("2023-11-14");
        result.Days[0].StartMills.Should().Be(1699916400000L);
        result.Days[0].EndMills.Should().Be(1700002800000L);
        result.Days[1].StartMills.Should().Be(result.Days[0].EndMills);
        result.Days.Select(d => d.Date).Should().Equal(result.StepDayTotals.Keys);
    }

    [Theory]
    [InlineData("2023-03-12", 23)]
    [InlineData("2023-03-13", 24)]
    [InlineData("2023-11-05", 25)]
    public async Task GetAsync_ServesADayAcrossADstChangeAtItsRealLength(string date, int hours)
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("America/New_York");
        var from = new DateTimeOffset(2023, 3, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(2023, 11, 7, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var march = await CreateService().GetAsync(from, from + 5L * 24 * 3_600_000);
        var november = await CreateService().GetAsync(to - 5L * 24 * 3_600_000, to);

        var day = march.Days.Concat(november.Days).First(d => d.Date == date);
        (day.EndMills - day.StartMills).Should().Be(hours * 3_600_000L);
    }

    [Theory]
    [InlineData("2023-03-11")]
    [InlineData("2023-03-12")]
    [InlineData("2023-03-13")]
    [InlineData("2023-11-04")]
    [InlineData("2023-11-05")]
    public async Task GetAsync_ServesHourMarksAtTheRealLocalHoursOfTheDayAndTheNext(string date)
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("America/New_York");
        var from = new DateTimeOffset(2023, 3, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var to = new DateTimeOffset(2023, 11, 7, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var march = await CreateService().GetAsync(from, from + 5L * 24 * 3_600_000);
        var november = await CreateService().GetAsync(to - 5L * 24 * 3_600_000, to);

        var day = march.Days.Concat(november.Days).First(d => d.Date == date);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var local = day.HourMarks
            .Select(m => TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(m).UtcDateTime, zone))
            .ToList();
        var first = DateTime.Parse(date, CultureInfo.InvariantCulture);
        local.Should().Equal(
            first.AddHours(6), first.AddHours(12), first.AddHours(18),
            first.AddDays(1).AddHours(6), first.AddDays(1).AddHours(12), first.AddDays(1).AddHours(18));
        day.HourMarks.Should().BeInAscendingOrder();
        day.HourMarks[0].Should().BeGreaterThan(day.StartMills);
        day.HourMarks[2].Should().BeLessThan(day.EndMills);
        day.HourMarks[3].Should().BeGreaterThan(day.EndMills);
    }

    [Fact]
    public async Task GetAsync_StepDayTotalsEqualTheStepsInsideEachServedDay()
    {
        SetupEmpty();
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetupZone("Pacific/Chatham");
        var from = new DateTimeOffset(2023, 11, 14, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var samples = Enumerable.Range(0, 96).Select(i => (from + i * 3_600_000L, 10 + i)).ToArray();
        SetupSteps(samples);

        var result = await CreateService().GetAsync(from, from + 96 * 3_600_000L);

        foreach (var day in result.Days)
        {
            var inside = samples.Where(s => s.Item1 >= day.StartMills && s.Item1 < day.EndMills).Sum(s => s.Item2);
            result.StepDayTotals[day.Date].Should().Be(inside);
        }
        result.StepDayTotals.Values.Sum().Should().Be(samples.Sum(s => s.Item2));
    }

    [Fact]
    public async Task GetAsync_PossibleRunningTotals_AreLeftOutOfBubblesAndDayTotals()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupSteps(
            (StartMills + 30 * 60_000, 300, 0),
            (StartMills + 40 * 60_000, 9_000, StepCount.PossibleRunningTotalFlag),
            (StartMills + 50 * 60_000, 9_400, StepCount.PossibleRunningTotalFlag | 1),
            (StartMills + 60 * 60_000, 500, 1));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.StepCounts.Select(s => (s.Time, s.Steps)).Should().Equal(
            (StartMills + 30 * 60_000, 300),
            (StartMills + 60 * 60_000, 500));
        result.StepDayTotals["2023-11-14"].Should().Be(800);
        result.StepDayTotals.Values.Sum().Should().Be(800);
    }

    [Fact]
    public async Task GetAsync_DoesNotInvokeBasalRateResolver()
    {
        // Guard rail: the actogram must not pull anything from the IOB/COB/basal pipeline.
        // No IBasalRateResolver mock is wired here — a regression that introduces it would
        // surface as a missing dependency at construction time.
        SetupEmpty();
        SetupZone("UTC");
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _targetRange.Setup(t => t.GetLowBGTargetAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(80);
        _targetRange.Setup(t => t.GetHighBGTargetAsync(It.IsAny<long>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(160);

        var act = async () => await CreateService().GetAsync(StartMills, EndMills);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetAsync_HeartRateSummary_CoversOnlyTheSummaryRange_HalfOpen()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupHeartRates(
            (SummaryStart - 1, 30),
            (SummaryStart, 80),
            (SummaryStart + 60_000, 60),
            (SummaryStart + 120_000, 100),
            (SummaryStart + 180_000, 70),
            (SummaryStart + 240_000, 90),
            (SummaryStart + 300_000, 65),
            (SummaryStart + 360_000, 75),
            (SummaryStart + 420_000, 85),
            (SummaryStart + 480_000, 95),
            (SummaryStart + 540_000, 55),
            (SummaryEnd, 200),
            (SummaryEnd + 60_000, 200));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills, SummaryStart, SummaryEnd);

        result.HeartRateSummary.Should().NotBeNull();
        var summary = result.HeartRateSummary!;
        summary.ReadingCount.Should().Be(10);
        summary.MinBpm.Should().Be(55);
        summary.MaxBpm.Should().Be(100);
        summary.AverageBpm.Should().Be(78, "the mean is 77.5, which rounds away from zero");
        summary.RestingBpm.Should().Be(60, "index 10 / 10 = 1 of the ascending readings");
        result.HeartRates.Should().HaveCount(13, "the series itself is not clipped to the summary range");
    }

    [Fact]
    public async Task GetAsync_HeartRateSummary_RestingBpmIsTheAscendingReadingAtCountOverTen()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupHeartRates(Enumerable.Range(0, 25)
            .Select(i => (SummaryStart + i * 60_000L, 125 - i * 2))
            .ToArray());
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills, SummaryStart, SummaryEnd);

        // Ascending readings are 77, 79, 81, ...; floor(25 / 10) = 2.
        result.HeartRateSummary!.RestingBpm.Should().Be(81);
        result.HeartRateSummary.MinBpm.Should().Be(77);
    }

    [Fact]
    public async Task GetAsync_HeartRateSummary_IsNullWhenNoReadingFallsInTheSummaryRange()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupHeartRates((SummaryStart - 1, 64), (SummaryEnd, 70));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills, SummaryStart, SummaryEnd);

        result.HeartRateSummary.Should().BeNull();
        result.HeartRates.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAsync_HeartRateSummary_IsNullWithNoReadings()
    {
        SetupEmpty();
        SetupZone("UTC");
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.HeartRateSummary.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_StepSummary_CoversOnlyTheSummaryRange()
    {
        SetupEmpty();
        SetupZone("Europe/Stockholm");
        SetupSteps(
            (SummaryStart - 1, 777),
            (SummaryStart, 300),
            (SummaryStart + 1_800_000, 500),
            (SummaryEnd, 999));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills, SummaryStart, SummaryEnd);

        result.StepSummary!.TotalSteps.Should().Be(800);
        result.StepSummary.DayCount.Should().Be(1);
        result.StepSummary.DailyAverage.Should().Be(800);
    }

    [Theory]
    [InlineData("Pacific/Auckland")]
    [InlineData("Europe/Stockholm")]
    [InlineData(null)]
    public async Task GetAsync_StepSummary_CountsTheRangesWholeDaysWhateverItsMidnights(string? zone)
    {
        // A 14-day pick aligned to Auckland midnights touches 15 UTC or Stockholm days.
        var aucklandMidnight = DateTimeOffset.Parse("2023-11-15T00:00:00+13:00").ToUnixTimeMilliseconds();
        SetupEmpty();
        SetupZone(zone);
        SetupSteps((aucklandMidnight, 1400));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(
            aucklandMidnight - 86_400_000, aucklandMidnight + 15 * 86_400_000L,
            aucklandMidnight, aucklandMidnight + 14 * 86_400_000L);

        result.StepSummary!.DayCount.Should().Be(14);
        result.StepSummary.DailyAverage.Should().Be(100);
    }

    [Fact]
    public async Task GetAsync_StepSummary_DailyAverageRoundsAwayFromZero()
    {
        SetupEmpty();
        SetupZone("Europe/Stockholm");
        SetupSteps((SummaryStart, 500), (SummaryStart + 1_800_000, 501));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(
            StartMills, EndMills, SummaryStart, SummaryStart + 2 * 86_400_000L);

        result.StepSummary!.TotalSteps.Should().Be(1001);
        result.StepSummary.DayCount.Should().Be(2);
        result.StepSummary.DailyAverage.Should().Be(501, "500.5 rounds up");
    }

    [Fact]
    public async Task GetAsync_StepSummary_CountsAtLeastOneDay()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupSteps((SummaryStart, 120));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills, SummaryStart, SummaryStart + 1);

        result.StepSummary!.DayCount.Should().Be(1);
        result.StepSummary.DailyAverage.Should().Be(120);
    }

    [Fact]
    public async Task GetAsync_StepSummary_ExcludesPossibleRunningTotals()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupSteps(
            (SummaryStart, 300, 0),
            (SummaryStart + 60_000, 9_000, StepCount.PossibleRunningTotalFlag));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills, SummaryStart, SummaryEnd);

        result.StepSummary!.TotalSteps.Should().Be(300);
    }

    [Fact]
    public async Task GetAsync_SummaryRangeOmitted_SummarisesTheWholeWindow()
    {
        SetupEmpty();
        SetupZone("UTC");
        SetupHeartRates((StartMills, 60), (StartMills + 3_600_000, 80), (EndMills - 1, 100));
        SetupSteps((StartMills, 100), (EndMills - 1, 200));
        _therapy.Setup(t => t.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = await CreateService().GetAsync(StartMills, EndMills);

        result.HeartRateSummary!.ReadingCount.Should().Be(3);
        result.HeartRateSummary.AverageBpm.Should().Be(80);
        result.StepSummary!.TotalSteps.Should().Be(300);
        result.StepSummary.DayCount.Should().Be(14);
        result.StepSummary.DailyAverage.Should().Be(21);
    }

    private void SetupHeartRates(params (long Mills, int Bpm)[] samples) =>
        _heartRates
            .Setup(h => h.GetHeartRateMinuteAveragesByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(samples
                .Select(s => new HeartRate
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime,
                    Bpm = s.Bpm,
                })
                .ToArray());

    private void SetupZone(string? id) =>
        _therapy
            .Setup(t => t.GetTimezoneAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(id);

    private void SetupSteps(params (long Mills, int Metric)[] samples) =>
        SetupSteps(samples.Select(s => (s.Mills, s.Metric, 0)).ToArray());

    private void SetupSteps(params (long Mills, int Metric, int Source)[] samples) =>
        _steps
            .Setup(s => s.GetStepCountsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(samples
                .Select(s => new StepCount
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(s.Mills).UtcDateTime,
                    Metric = s.Metric,
                    Source = s.Source,
                })
                .ToArray());

    private void SetupEmpty()
    {
        _glucose
            .Setup(g => g.GetForAnalyticsAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), null, null,
                It.IsAny<int>(), 0, false, It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
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
            .Setup(h => h.GetHeartRateMinuteAveragesByDateRangeAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HeartRate>());
    }
}
