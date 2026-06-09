using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.Core.Contracts.Profiles.Resolvers;
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

    /// <summary>
    /// The caller (e.g. IobCobComputeStage) builds a TherapyTimeline once and passes it
    /// into BuildAsync. The builder must use that supplied timeline rather than calling
    /// the resolver again — both for performance and to guarantee the basal series sees
    /// the same therapy state as IOB/COB.
    /// </summary>
    [Fact]
    public async Task BuildAsync_UsesSuppliedTimeline()
    {
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;
        const double defaultBasalRate = 1.0;

        var suppliedTimeline = new TherapyTimeline(new[]
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
                    basalEntries: [new ScheduleEntry { TimeAsSeconds = 0, Value = 1.7 }]))
        });

        var therapySettings = new Mock<ITherapySettingsResolver>();
        therapySettings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var builder = new BasalSeriesBuilder(
            therapySettings.Object,
            NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(
            tempBasals: [],
            startTime,
            endTime,
            defaultBasalRate,
            suppliedTimeline,
            CancellationToken.None);

        // Output is driven by the supplied timeline's 1.7 U/hr schedule entry, not the default.
        result.Should().NotBeEmpty();
        result.Should().AllSatisfy(p => p.Rate.Should().BeApproximately(1.7, 0.001));
    }
}
