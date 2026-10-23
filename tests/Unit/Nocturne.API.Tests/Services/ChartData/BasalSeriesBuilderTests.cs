using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.ChartData;

public class BasalSeriesBuilderTests
{
    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    private static Mock<IBasalSegmentService> ScheduleOf(params (long Start, long End, double Rate)[] segments)
    {
        var service = new Mock<IBasalSegmentService>();
        service
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((long from, long to, CancellationToken _) => segments
                .Where(g => g.End > from && g.Start < to)
                .Select(g => new BasalSegment(Math.Max(g.Start, from), Math.Min(g.End, to), g.Rate, g.Rate, "Default"))
                .ToAsyncEnumerable());
        return service;
    }

    private static Mock<IBasalSegmentService> FlatSchedule(double rate) =>
        ScheduleOf((long.MinValue, long.MaxValue, rate));

    [Fact]
    public async Task BuildAsync_FillsFromTheSegmentSource()
    {
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var builder = new BasalSeriesBuilder(
            therapySettings.Object, FlatSchedule(1.7).Object, NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(tempBasals: [], startTime, endTime, 1.0, CancellationToken.None);

        result.Should().NotBeEmpty();
        result.Should().AllSatisfy(p => p.Rate.Should().BeApproximately(1.7, 0.001));
    }

    [Fact]
    public async Task BuildAsync_RowStartedBeforeWindow_IsClippedToWindowStart()
    {
        var startTime = TestMills;
        var endTime = TestMills + 60 * 60 * 1000;
        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime - 20 * 60 * 1000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + 10 * 60 * 1000).UtcDateTime,
            Rate = 0.4,
            Origin = TempBasalOrigin.Algorithm,
        };
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var builder = new BasalSeriesBuilder(therapySettings.Object, FlatSchedule(1.2).Object, NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync([tempBasal], startTime, endTime, 1.0, CancellationToken.None);

        result[0].Timestamp.Should().Be(startTime);
        result[0].Rate.Should().BeApproximately(0.4, 0.001);
        result[1].Timestamp.Should().Be(startTime + 10 * 60 * 1000);
        result[1].Rate.Should().BeApproximately(1.2, 0.001);
    }

    [Fact]
    public async Task BuildAsync_RelativeTempOfUnknownRate_CancelsThePreviousTempAndShowsTheSchedule()
    {
        var startTime = TestMills;
        var endTime = TestMills + 60 * 60 * 1000;
        TempBasal Temp(int fromMinutes, int toMinutes, double? rate, double? percent) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + fromMinutes * 60_000L).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + toMinutes * 60_000L).UtcDateTime,
            Rate = rate,
            Percent = percent,
            Origin = TempBasalOrigin.Manual,
        };
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var builder = new BasalSeriesBuilder(therapySettings.Object, FlatSchedule(1.2).Object, NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(
            [Temp(0, 30, 0.4, null), Temp(10, 40, null, -50)], startTime, endTime, 1.0, CancellationToken.None);

        result[0].Timestamp.Should().Be(startTime);
        result[0].Rate.Should().BeApproximately(0.4, 0.001);
        result[1].Timestamp.Should().Be(startTime + 10 * 60_000L);
        result[1].Rate.Should().BeApproximately(1.2, 0.001);
        result[1].Origin.Should().Be(BasalDeliveryOrigin.Inferred);
        result.Should().NotContain(p => p.Timestamp > startTime && p.Rate < 1.2 - 0.001);
    }

    /// <summary>
    /// Injection (MDI) therapy produces no TempBasal records and often no therapy
    /// profile either. The builder used to fall back to a flat 1 U/hr band, which
    /// the chart draws identically to pump-confirmed delivery — inventing basal the
    /// wearer never received. With no therapy settings there must be no series.
    /// </summary>
    [Fact]
    public async Task BuildAsync_WithoutTherapySettings_ReturnsNoPointsRatherThanAFlatDefault()
    {
        var startTime = TestMills;
        var endTime = TestMills + 6 * 60 * 60 * 1000;
        const double defaultBasalRate = 1.0;

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var builder = new BasalSeriesBuilder(
            therapySettings.Object,
            FlatSchedule(1.0).Object,
            NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(
            tempBasals: [],
            startTime,
            endTime,
            defaultBasalRate,
            CancellationToken.None);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Real TempBasal records are pump-confirmed delivery and must still be drawn
    /// when the therapy profile is missing — only the inferred fill is suppressed.
    /// </summary>
    [Fact]
    public async Task BuildAsync_WithoutTherapySettings_StillReturnsRecordedTempBasals()
    {
        var startTime = TestMills;
        var endTime = TestMills + 60 * 60 * 1000;

        // StartMills/EndMills are computed from the timestamps, so drive those.
        var tempBasal = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + 10 * 60 * 1000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTime + 40 * 60 * 1000).UtcDateTime,
            Rate = 0.65,
            Origin = TempBasalOrigin.Algorithm,
        };

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var builder = new BasalSeriesBuilder(
            therapySettings.Object,
            FlatSchedule(1.0).Object,
            NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(
            tempBasals: [tempBasal],
            startTime,
            endTime,
            defaultBasalRate: 1.0,
            CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Rate.Should().BeApproximately(0.65, 0.001);
        // No profile means no scheduled rate to compare against, not a placeholder one.
        result[0].ScheduledRate.Should().BeNull();
    }

    private const long Midnight = 1700006400000L;
    private const long Minute = 60_000L;
    private const long SixAm = Midnight + 6 * 60 * Minute;

    private static Task<List<BasalPoint>> BuildGapAsync(
        Mock<IBasalSegmentService> schedule, long startTime, long endTime, params TempBasal[] temps)
    {
        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var builder = new BasalSeriesBuilder(
            therapySettings.Object, schedule.Object, NullLogger<BasalSeriesBuilder>.Instance);
        return builder.BuildAsync(
            [.. temps], startTime, endTime, 1.0, CancellationToken.None);
    }

    [Fact]
    public async Task BuildAsync_ScheduleChangeInsideAGap_StepsAtTheExactInstant()
    {
        var start = SixAm - 2 * Minute;
        var end = SixAm + 20 * Minute;
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(schedule, start, end);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal((start, 1.0), (SixAm, 2.0));
        result.Should().AllSatisfy(p => p.Origin.Should().Be(BasalDeliveryOrigin.Inferred));
    }

    [Fact]
    public async Task BuildAsync_ProfileSwitchInsideAGap_StepsAtTheSwitchInstant()
    {
        var start = SixAm - 7 * Minute;
        var switchAt = SixAm + 3 * Minute + 17_000;
        var end = SixAm + 30 * Minute;
        var schedule = new Mock<IBasalSegmentService>();
        schedule
            .Setup(s => s.GetSegmentsAsync(start, end, It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new BasalSegment(start, switchAt, 0.8, 0.8, "Weekday"),
                new BasalSegment(switchAt, end, 1.4, 1.4, "Weekend"),
            }.ToAsyncEnumerable());

        var result = await BuildGapAsync(schedule, start, end);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal((start, 0.8), (switchAt, 1.4));
    }

    [Fact]
    public async Task BuildAsync_PercentageChangeInsideAGap_StepsAtTheChangeInstant()
    {
        var start = SixAm;
        var changeAt = SixAm + 11 * Minute + 30_000;
        var end = SixAm + 40 * Minute;
        var schedule = new Mock<IBasalSegmentService>();
        schedule
            .Setup(s => s.GetSegmentsAsync(start, end, It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new BasalSegment(start, changeAt, 1.0, 1.0, "Default"),
                new BasalSegment(changeAt, end, 1.5, 1.0, "Default"),
            }.ToAsyncEnumerable());

        var result = await BuildGapAsync(schedule, start, end);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal((start, 1.0), (changeAt, 1.5));
    }

    [Fact]
    public async Task BuildAsync_GapWithNoBoundary_IsOnePoint()
    {
        var start = SixAm + Minute;
        var end = SixAm + 50 * Minute;
        var schedule = new Mock<IBasalSegmentService>();
        schedule
            .Setup(s => s.GetSegmentsAsync(start, end, It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new BasalSegment(start, SixAm + 20 * Minute, 1.0, 1.0, "Default"),
                new BasalSegment(SixAm + 20 * Minute, end, 1.0, 1.0, "Default"),
            }.ToAsyncEnumerable());

        var result = await BuildGapAsync(schedule, start, end);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal((start, 1.0));
    }

    [Fact]
    public async Task BuildAsync_GapBetweenTempBasals_StepsAtTheScheduleBoundary()
    {
        var start = SixAm - 20 * Minute;
        var end = SixAm + 40 * Minute;
        TempBasal Temp(long from, long to) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Rate = 0.3,
            Origin = TempBasalOrigin.Algorithm,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(
            schedule, start, end, Temp(start, SixAm - 8 * Minute), Temp(SixAm + 10 * Minute, end));

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal(
            (start, 0.3), (SixAm - 8 * Minute, 1.0), (SixAm, 2.0), (SixAm + 10 * Minute, 0.3));
    }

    [Fact]
    public async Task BuildAsync_SeveralGaps_FetchTheScheduleOnce()
    {
        var start = SixAm - 30 * Minute;
        var end = SixAm + 60 * Minute;
        TempBasal Temp(long from, long to) => new()
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(from).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(to).UtcDateTime,
            Rate = 0.3,
            Origin = TempBasalOrigin.Algorithm,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(
            schedule, start, end,
            Temp(SixAm - 20 * Minute, SixAm - 10 * Minute),
            Temp(SixAm + 5 * Minute, SixAm + 15 * Minute),
            Temp(SixAm + 30 * Minute, SixAm + 40 * Minute));

        schedule.Verify(
            s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal(
            (start, 1.0),
            (SixAm - 20 * Minute, 0.3),
            (SixAm - 10 * Minute, 1.0),
            (SixAm, 2.0),
            (SixAm + 5 * Minute, 0.3),
            (SixAm + 15 * Minute, 2.0),
            (SixAm + 30 * Minute, 0.3),
            (SixAm + 40 * Minute, 2.0));
    }

    [Fact]
    public async Task BuildAsync_ZeroLengthRelativeTempOfUnknownRate_AddsNoPoint()
    {
        var start = SixAm - 20 * Minute;
        var end = SixAm + 40 * Minute;
        var at = end;
        var relative = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(at).UtcDateTime,
            Percent = -50,
            Origin = TempBasalOrigin.Manual,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(schedule, start, end, relative);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal((start, 1.0), (SixAm, 2.0));
    }

    [Fact]
    public async Task BuildAsync_RecordedTempWithoutScheduledRate_TakesItFromTheCoveringSegment()
    {
        var start = SixAm - 20 * Minute;
        var end = SixAm + 40 * Minute;
        var temp = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm + 5 * Minute).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm + 15 * Minute).UtcDateTime,
            Rate = 0.3,
            Origin = TempBasalOrigin.Algorithm,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(schedule, start, end, temp);

        result.Single(p => p.Origin == BasalDeliveryOrigin.Algorithm).ScheduledRate.Should().Be(2.0);
    }

    [Fact]
    public async Task BuildAsync_TempEndingBeforeItStarts_FillsFromTheScheduleAtItsEnd()
    {
        var start = SixAm - 20 * Minute;
        var end = SixAm + 30 * Minute;
        var inverted = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm + 10 * Minute).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm - 5 * Minute).UtcDateTime,
            Rate = 0.5,
            Origin = TempBasalOrigin.Algorithm,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(schedule, start, end, inverted);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal(
            (start, 1.0),
            (SixAm, 2.0),
            (SixAm + 10 * Minute, 0.5),
            (SixAm - 5 * Minute, 1.0),
            (SixAm, 2.0));
    }

    [Fact]
    public async Task BuildAsync_GapStartingOnAScheduleBoundary_IsOnePointAtTheNewRate()
    {
        var start = SixAm - 20 * Minute;
        var end = SixAm + 20 * Minute;
        var temp = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm).UtcDateTime,
            Rate = 0.3,
            Origin = TempBasalOrigin.Algorithm,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(schedule, start, end, temp);

        result.Select(p => (p.Timestamp, p.Rate)).Should().Equal((start, 0.3), (SixAm, 2.0));
    }

    [Fact]
    public async Task BuildAsync_RecordedTempStartingOnAScheduleBoundary_TakesTheNewSegmentRate()
    {
        var start = SixAm - 20 * Minute;
        var end = SixAm + 40 * Minute;
        var temp = new TempBasal
        {
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(SixAm + 10 * Minute).UtcDateTime,
            Rate = 0.3,
            Origin = TempBasalOrigin.Algorithm,
        };
        var schedule = ScheduleOf((Midnight, SixAm, 1.0), (SixAm, Midnight + 24 * 60 * Minute, 2.0));

        var result = await BuildGapAsync(schedule, start, end, temp);

        result.Single(p => p.Origin == BasalDeliveryOrigin.Algorithm).ScheduledRate.Should().Be(2.0);
    }
}
