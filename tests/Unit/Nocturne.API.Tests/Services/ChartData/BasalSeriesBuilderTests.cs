using FluentAssertions;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.ChartData;

public class BasalSeriesBuilderTests
{
    // Common test timestamp: 2023-11-15T00:00:00Z in millis
    private const long TestMills = 1700000000000L;

    [Fact]
    public void BuildFromProfile_WithHasData_UsesTimelineNotResolver()
    {
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;
        const double defaultBasalRate = 1.0;

        var timeline = new TherapyTimeline(new[]
        {
            new TherapySegment(startTime, endTime + 1,
                new TherapySnapshot(
                    dia: 3.0,
                    peakMinutes: TherapySnapshot.DefaultPeakMinutes,
                    carbsPerHour: TherapySnapshot.DefaultCarbsPerHour,
                    timezone: null,
                    ccpPercentage: null,
                    ccpTimeshiftMs: 0,
                    sensitivityEntries: null,
                    carbRatioEntries: null,
                    basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 1.2 }]))
        });

        // Act
        var result = BasalSeriesBuilder.BuildFromProfile(
            startTime, endTime, defaultBasalRate, timeline, hasData: true);

        // Assert: rate comes from the timeline's basal schedule entry (1.2 U/hr), not the default
        result.Should().NotBeEmpty();
        result.Should().AllSatisfy(p => p.Rate.Should().BeApproximately(1.2, 0.001));
    }
}
