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

    /// <summary>
    /// The timeline a tenant with no therapy profile still yields: one segment whose
    /// snapshot carries no basal schedule. Its <c>BasalRateAt</c> answers the model's
    /// own 1.0 placeholder, so a test asserting emptiness proves the hasData guard
    /// rather than an empty timeline.
    /// </summary>
    private static TherapyTimeline ProfilelessTimeline(long startTime, long endTime) =>
        new([
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
                    basalEntries: null))
        ]);

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
            NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(
            tempBasals: [],
            startTime,
            endTime,
            defaultBasalRate,
            ProfilelessTimeline(startTime, endTime),
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
            NullLogger<BasalSeriesBuilder>.Instance);

        var result = await builder.BuildAsync(
            tempBasals: [tempBasal],
            startTime,
            endTime,
            defaultBasalRate: 1.0,
            ProfilelessTimeline(startTime, endTime),
            CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Rate.Should().BeApproximately(0.65, 0.001);
        // No profile means no scheduled rate to compare against, not a placeholder one.
        result[0].ScheduledRate.Should().BeNull();
    }

    /// <summary>
    /// The gap-filling path is what produced the flat band: with a profile present it
    /// still fills from the schedule, so the suppression must be keyed on hasData only.
    /// </summary>
    [Fact]
    public void BuildFromProfile_WithoutHasData_EmitsNothing()
    {
        var startTime = TestMills;
        var endTime = TestMills + 30 * 60 * 1000;

        var result = BasalSeriesBuilder.BuildFromProfile(
            startTime, endTime, defaultBasalRate: 1.0,
            ProfilelessTimeline(startTime, endTime), hasData: false);

        result.Should().BeEmpty();
    }
}
