using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Clinical.Tests.Statistics;

public class StatisticsServiceClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static SensorGlucose Reading(DateTimeOffset at, double mgdl) =>
        new() { Mgdl = mgdl, Timestamp = at.UtcDateTime };

    private static GlucoseAnalytics Analyse(StatisticsService service, params SensorGlucose[] entries) =>
        service.AnalyzeGlucoseData(entries, [], [], windowDays: 14);

    [Fact]
    public void TimeOfAnalysis_is_the_injected_clock_when_there_are_readings()
    {
        var service = new StatisticsService(new FakeTimeProvider(Now));

        var result = Analyse(service, Reading(Now.AddMinutes(-5), 110), Reading(Now, 120));

        result.Time.TimeOfAnalysis.Should().Be(Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void TimeOfAnalysis_is_the_injected_clock_when_there_are_no_readings()
    {
        var service = new StatisticsService(new FakeTimeProvider(Now));

        var result = Analyse(service);

        result.Time.TimeOfAnalysis.Should().Be(Now.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void TimeOfAnalysis_follows_the_clock_and_nothing_else_changes()
    {
        var clock = new FakeTimeProvider(Now);
        var service = new StatisticsService(clock);
        var readings = new[] { Reading(Now.AddMinutes(-5), 110), Reading(Now, 120) };

        var first = Analyse(service, readings);
        clock.Advance(TimeSpan.FromHours(1));
        var second = Analyse(service, readings);

        second.Time.TimeOfAnalysis.Should().Be(first.Time.TimeOfAnalysis + 3_600_000);
        second.Time.Start.Should().Be(first.Time.Start);
        second.Time.End.Should().Be(first.Time.End);
        second.BasicStats.Mean.Should().Be(first.BasicStats.Mean);
    }
}
