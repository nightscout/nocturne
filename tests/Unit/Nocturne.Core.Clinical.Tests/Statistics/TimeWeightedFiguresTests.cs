using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Statistics;

/// <summary>
/// An hour of one-minute readings in range beside an hour of five-minute readings above it is half
/// the time in range, whatever the reading counts say. Every figure that reports a share of time
/// has to agree.
/// </summary>
public class TimeWeightedFiguresTests
{
    private static readonly DateTime Start = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);

    private readonly StatisticsService _sut = new(TimeProvider.System);

    private static SensorGlucose Reading(DateTime at, double mgdl) => new() { Timestamp = at, Mgdl = mgdl };

    private static List<SensorGlucose> OneMinuteThenFiveMinute(
        DateTime start, int oneMinuteReadings, double inRange, int fiveMinuteReadings, double above) =>
        Enumerable
            .Range(0, oneMinuteReadings)
            .Select(i => Reading(start.AddMinutes(i), inRange))
            .Concat(Enumerable
                .Range(0, fiveMinuteReadings)
                .Select(i => Reading(start.AddMinutes(oneMinuteReadings + i * 5), above)))
            .ToList();

    [Fact]
    public void CalculateTimeInRange_WeighsMixedCadencesByTime()
    {
        var readings = OneMinuteThenFiveMinute(Start, 60, 120, 12, 220);

        var percentages = _sut.CalculateTimeInRange(readings).Percentages;

        percentages.Target.Should().BeApproximately(50, 1e-9);
        percentages.High.Should().BeApproximately(50, 1e-9);
    }

    [Fact]
    public void CalculatePGS_TakesTheTimeWeightedPercentage()
    {
        var readings = OneMinuteThenFiveMinute(Start, 60, 120, 12, 220);
        var values = readings.Select(r => r.Mgdl).ToList();
        var mean = values.Average();

        var result = _sut.CalculateGlycemicVariability(values, readings, TimeZoneInfo.Utc);

        result.Should().NotBeNull();
        var fractionOutOfRange = result!.PatientGlycemicStatus
            / (result.GlycemicVariabilityIndex * mean);
        fractionOutOfRange.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void CalculatePGS_ScalesGviAndMeanByTheTimeOutOfRange()
    {
        _sut.CalculatePGS(100, 1.0, 100).Should().Be(0);
        _sut.CalculatePGS(50, 1.5, 175).Should().BeApproximately(131.25, 1e-9);
    }

    [Fact]
    public void HourlyBands_WeighAnHourMixingSensorCadencesByTime()
    {
        var readings = OneMinuteThenFiveMinute(Start, 30, 100, 6, 220);

        var hour = _sut.CalculateAveragedStats(readings, TimeZoneInfo.Utc).Single(h => h.Hour == 10);

        hour.TimeInRange.TightTarget.Should().Be(50);
        hour.TimeInRange.High.Should().Be(50);
    }

    [Fact]
    public void HourlyPatterns_WeighAnHourMixingSensorCadencesByTime()
    {
        var readings = OneMinuteThenFiveMinute(Start, 30, 100, 6, 220);

        var hour = _sut.CalculateHourlyPatterns(readings, TimeZoneInfo.Utc).Hours.Single(h => h.Hour == 10);

        hour.InRange.Should().Be(50);
        hour.AboveRange.Should().Be(50);
    }

    [Fact]
    public void SiteChangeImpact_WeighsEachSideOfTheChangeByTime()
    {
        var firstChange = Start;
        var secondChange = Start.AddDays(10);
        var readings = new List<SensorGlucose>();
        foreach (var change in new[] { firstChange, secondChange })
        {
            readings.AddRange(OneMinuteThenFiveMinute(change.AddMinutes(-60), 30, 120, 6, 220));
            readings.AddRange(Enumerable.Range(0, 13).Select(i => Reading(change.AddMinutes(i * 5), 120)));
        }
        readings.AddRange(Enumerable.Range(0, 100).Select(i => Reading(Start.AddDays(30).AddMinutes(i * 5), 120)));
        DeviceEvent[] events =
        [
            new() { Timestamp = firstChange, EventType = DeviceEventType.SiteChange },
            new() { Timestamp = secondChange, EventType = DeviceEventType.SiteChange },
        ];

        var summary = _sut.CalculateSiteChangeImpact(readings, events, 1, 1, 30).Summary;

        summary.Should().NotBeNull();
        summary!.TimeInRangeBeforeChange.Should().Be(50);
        summary.TimeInRangeAfterChange.Should().Be(100);
    }
}
