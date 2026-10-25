using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ChartData;
using Nocturne.API.Services.V4;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Delivered basal around a v1 cancel, as Loop Analysis reads it: the temps clipped, the series
/// built from them over the schedule, and delivery read off that series.
/// </summary>
public class LoopAnalysisTempBasalCancelTests
{
    private const long Minute = 60_000;
    private const long From = 1_700_000_000_000;
    private const long To = From + 60 * Minute;

    [Fact]
    public async Task AfterAV1Cancel_TheScheduleIsDeliveredAtOnce()
    {
        var cancel = TreatmentDecomposer.MapToTempBasal(
            new Treatment { EventType = "Temp Basal", Mills = From + 10 * Minute, Duration = 0, Absolute = 0, EnteredBy = "pump" },
            correlationId: null);

        var (clipped, evidence) = NetInsulinAboveSchedule.ClipTempBasals(
            [Temp(From, 30), cancel, Temp(From + 40 * Minute, 20)], To);
        var settings = new Mock<ITherapySettingsResolver>();
        settings.Setup(s => s.HasDataAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var schedule = new Mock<IBasalSegmentService>();
        schedule
            .Setup(s => s.GetSegmentsAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((long from, long to, CancellationToken _) =>
                new[] { new BasalSegment(from, to, 1.0, 1.0, "Default") }.ToAsyncEnumerable());
        var series = await new BasalSeriesBuilder(settings.Object, schedule.Object, NullLogger<BasalSeriesBuilder>.Instance)
            .BuildAsync(clipped, From, To, 1.0, []);

        NetInsulinAboveSchedule.DeliveredFromSeries(series, To, evidence)
            .Select(d => (d.Start, d.End, d.Rate))
            .Should().Equal(
                (From, From + 10 * Minute, 2.0),
                (From + 10 * Minute, From + 40 * Minute, 1.0),
                (From + 40 * Minute, To, 2.0));
    }

    private static TempBasal Temp(long start, int minutes) => new()
    {
        StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start).UtcDateTime,
        EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(start + minutes * Minute).UtcDateTime,
        Rate = 2.0,
        Origin = TempBasalOrigin.Algorithm,
        Device = "pump",
    };
}
