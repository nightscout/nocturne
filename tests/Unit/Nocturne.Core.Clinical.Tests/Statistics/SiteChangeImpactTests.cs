using FluentAssertions;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class SiteChangeImpactTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowsIncludeDuplicateEndpointsAndExcludeAdjacentReadings(bool reverse)
    {
        var change = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = Enumerable.Range(0, 100).Select(i => new SensorGlucose
        {
            Timestamp = change.AddDays(30).AddMinutes(i), Mgdl = 200,
        }).ToList();
        foreach (var (timestamp, glucose) in new[]
        {
            (change.AddHours(-1), 110.0), (change, 120.0), (change.AddHours(1), 130.0),
        })
        {
            readings.Add(new SensorGlucose { Timestamp = timestamp, Mgdl = glucose });
            readings.Add(new SensorGlucose { Timestamp = timestamp, Mgdl = glucose });
        }
        readings.Add(new SensorGlucose { Timestamp = change.AddHours(-1).AddMilliseconds(-1), Mgdl = 300 });
        readings.Add(new SensorGlucose { Timestamp = change.AddHours(1).AddMilliseconds(1), Mgdl = 300 });
        if (reverse) readings.Reverse();
        var events = new[]
        {
            new DeviceEvent { Timestamp = change, EventType = DeviceEventType.SiteChange },
            new DeviceEvent { Timestamp = change.AddDays(10), EventType = DeviceEventType.PodChange },
        };

        var result = new StatisticsService(TimeProvider.System).CalculateSiteChangeImpact(readings, events, 1, 1, 30);

        result.SiteChangeCount.Should().Be(2);
        result.DataPoints.Select(p => p.MinutesFromChange).Should().Equal(-60, 0, 30);
        result.DataPoints.Select(p => p.Count).Should().Equal(2, 2, 2);
        result.DataPoints.Select(p => p.AverageGlucose).Should().Equal(110, 120, 130);
    }

    [Fact]
    public void ServesTheConsensusEdgesTheChartDrawsItsTargetRangeBetween()
    {
        var result = new StatisticsService(TimeProvider.System).CalculateSiteChangeImpact([], []);

        result.Thresholds.Should().BeEquivalentTo(new StatisticsService(TimeProvider.System).HourlyBandThresholds);
        result.Thresholds.VeryLow.Should().Be(54);
        result.Thresholds.TargetBottom.Should().Be(70);
        result.Thresholds.TargetTop.Should().Be(180);
        result.Thresholds.VeryHigh.Should().Be(250);
    }
}
