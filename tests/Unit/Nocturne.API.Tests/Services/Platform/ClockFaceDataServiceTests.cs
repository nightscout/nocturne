using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Widget;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// A clock face reads only the parts its layout holds, and only those its reader may read.
/// </summary>
[Trait("Category", "Unit")]
public class ClockFaceDataServiceTests
{
    private readonly Mock<IIobCobReader> _iobCob = new(MockBehavior.Strict);
    private readonly Mock<IChartDataService> _charts = new(MockBehavior.Strict);
    private readonly Mock<ISensorGlucoseRepository> _glucose = new(MockBehavior.Strict);
    private readonly Mock<IStatisticsService> _statistics = new(MockBehavior.Strict);
    private readonly Mock<ITrackerRepository> _trackers = new(MockBehavior.Strict);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero));
    private readonly Mock<IPredictionService> _predictions = new(MockBehavior.Strict);

    private ClockFaceDataService Service() => new(
        _iobCob.Object, _charts.Object, _glucose.Object, _statistics.Object, _trackers.Object,
        new ConfigurationBuilder().Build(), _time, NullLogger<ClockFaceDataService>.Instance, _predictions.Object);

    [Fact]
    public async Task AFaceWithNoDataElements_ReadsNothing()
    {
        var data = await Service().ReadAsync(
            Face(new ClockElement { Type = "sg" }, new ClockElement { Type = "time" }),
            AllScopes, CancellationToken.None);

        data.Iob.Should().BeNull();
        data.Basal.Should().BeNull();
        data.Summaries.Should().BeEmpty();
        data.Trackers.Should().BeEmpty();
    }

    [Fact]
    public async Task AReaderWithoutTheScope_ReadsNoneOfThePart()
    {
        var data = await Service().ReadAsync(
            Face(
                new ClockElement { Type = "iob" },
                new ClockElement { Type = "basal" },
                new ClockElement { Type = "trackers" },
                new ClockElement { Type = "forecast" }),
            new HashSet<string> { Scope.GlucoseRead }, CancellationToken.None);

        data.Iob.Should().BeNull();
        data.Basal.Should().BeNull();
        data.Trackers.Should().BeEmpty();
        data.Forecasts.Should().BeEmpty();
    }

    [Fact]
    public async Task ASummary_ReadsTimeInRangeOverTheHoursItNames()
    {
        _glucose.Setup(g => g.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(),
                It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .ReturnsAsync([new SensorGlucose(), new SensorGlucose()]);
        _statistics.Setup(s => s.CalculateTimeInRange(It.IsAny<IEnumerable<SensorGlucose>>(), null))
            .Returns(new TimeInRangeMetrics
            {
                Percentages = new TimeInRangePercentages { VeryLow = 1, Low = 2, Target = 80, High = 12, VeryHigh = 5 },
            });

        var data = await Service().ReadAsync(
            Face(new ClockElement { Type = "summary", Hours = 6 }, new ClockElement { Type = "summary", Hours = 7 }),
            new HashSet<string> { Scope.GlucoseRead }, CancellationToken.None);

        data.Summaries.Select(s => s.Hours).Should().BeEquivalentTo([6, 3]);
        data.Summaries.Should().AllSatisfy(s =>
        {
            s.InRangePercent.Should().Be(80);
            s.BelowPercent.Should().Be(3);
            s.AbovePercent.Should().Be(17);
            s.Readings.Should().Be(2);
        });
    }

    [Fact]
    public async Task ATrackerElement_ReadsOnlyTheTrackerItNames()
    {
        var site = Guid.NewGuid();
        var sensor = Guid.NewGuid();
        _trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Running(site, "Site"), Running(sensor, "Sensor")]);

        var data = await Service().ReadAsync(
            Face(new ClockElement { Type = "tracker", DefinitionId = site }),
            new HashSet<string> { Scope.AlertsRead }, CancellationToken.None);

        data.Trackers.Select(t => t.Name).Should().Equal("Site");
    }

    [Fact]
    public async Task ATrackersElement_ServesOnlyTheTrackersItShows()
    {
        _trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                Running(Guid.NewGuid(), "Site", TrackerCategory.Cannula),
                Running(Guid.NewGuid(), "Sensor", TrackerCategory.Sensor),
            ]);

        var data = await Service().ReadAsync(
            Face(new ClockElement { Type = "trackers", Categories = ["Sensor"] }),
            new HashSet<string> { Scope.AlertsRead }, CancellationToken.None);

        // Anyone holding the link reads the payload, so a tracker no element shows is never in it.
        data.Trackers.Select(t => t.Name).Should().Equal("Sensor");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("always", true)]
    [InlineData("info", true)]
    [InlineData("warn", false)]
    [InlineData("urgent", false)]
    public void ATrackerBelowTheElementsThreshold_IsNotShown(string? threshold, bool shown)
    {
        var tracker = new V4TrackerStatus { Category = TrackerCategory.Sensor, Urgency = NotificationUrgency.Info };

        ClockFaceDataService.Shows(new ClockElement { Type = "trackers", VisibilityThreshold = threshold }, tracker)
            .Should().Be(shown);
    }

    [Theory]
    [InlineData(0, 30, 6)]
    [InlineData(25, 30, 11)]
    [InlineData(25, 60, 17)]
    public async Task AForecast_IsReadFromNow_NotFromTheStartOfTheCurve(int curveAgeMinutes, int minutesAhead, int index)
    {
        _predictions.Setup(p => p.GetPredictionsAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GlucosePredictionResponse
            {
                Timestamp = _time.GetUtcNow().AddMinutes(-curveAgeMinutes),
                IntervalMinutes = 5,
                Predictions = new PredictionCurves { Default = [.. Enumerable.Range(0, 48).Select(i => 100.0 + i)] },
            });

        var data = await Service().ReadAsync(
            Face(new ClockElement { Type = "forecast", MinutesAhead = minutesAhead }),
            AllScopes, CancellationToken.None);

        data.Forecasts.Should().ContainSingle().Which.Mgdl.Should().Be(100 + index);
    }

    [Fact]
    public async Task AChart_CarriesNoFieldItsTracksDoNotDraw()
    {
        _charts.Setup(c => c.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), 5, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardChartData
            {
                GlucoseData = [new GlucosePointDto { Time = 1, Sgv = 120, DataSource = "xdrip", Direction = "Flat" }],
                BolusMarkers = [new BolusMarkerDto { Time = 1, Insulin = 2, TreatmentId = "t1", DataSource = "loop" }],
                CarbMarkers = [new CarbMarkerDto { Time = 1, Carbs = 40, Label = "Pizza at Joe's", TreatmentId = "t2" }],
                DeviceEventMarkers = [new DeviceEventMarkerDto { Time = 1, Notes = "Site on left arm", TreatmentId = "t3" }],
                BgCheckMarkers = [new BgCheckMarkerDto { Time = 1, Glucose = 110 }],
                SystemEventMarkers = [new SystemEventMarkerDto { Id = "s1", Code = "E12" }],
                PumpModeSpans = [new ChartStateSpanDto { Id = "p1", State = "Suspended" }],
                ProfileSpans = [new ChartStateSpanDto { Id = "p2", State = "Weekend" }],
                TempBasalSpans =
                [
                    new ChartStateSpanDto
                    {
                        Id = "b1",
                        Metadata = new Dictionary<string, object> { ["rate"] = 0.4, ["notes"] = "Exercise" },
                    },
                ],
            });

        var chart = (await Service().ReadChartAsync(
            Face(new ClockElement
            {
                Type = "chart",
                ChartConfig = new ClockChartConfig { ShowCarbs = true, ShowBasal = true, ShowDeviceEvents = true },
            }),
            AllScopes, CancellationToken.None)).Chart!;

        chart.GlucoseData.Should().ContainSingle().Which.DataSource.Should().BeNull();
        chart.BolusMarkers.Should().ContainSingle().Which.TreatmentId.Should().BeNull();
        chart.BolusMarkers[0].DataSource.Should().BeNull();
        chart.CarbMarkers.Should().ContainSingle().Which.Label.Should().BeNull();
        chart.DeviceEventMarkers.Should().ContainSingle().Which.Notes.Should().BeNull();
        chart.BgCheckMarkers.Should().BeEmpty();
        chart.SystemEventMarkers.Should().BeEmpty();
        chart.PumpModeSpans.Should().BeEmpty();
        chart.ProfileSpans.Should().BeEmpty();
        chart.TempBasalSpans.Should().ContainSingle().Which.Metadata.Should().BeEquivalentTo(
            new Dictionary<string, object> { ["rate"] = 0.4 });
    }

    [Fact]
    public async Task AFaceWithNoChart_ReadsNoChart()
    {
        var chart = await Service().ReadChartAsync(
            Face(new ClockElement { Type = "iob" }), AllScopes, CancellationToken.None);

        chart.Chart.Should().BeNull();
        chart.Predictions.Should().BeNull();
    }

    [Fact]
    public async Task AChart_CarriesOnlyTheTracksItDraws()
    {
        _charts.Setup(c => c.GetDashboardChartDataAsync(
                It.IsAny<long>(), It.IsAny<long>(), 5, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardChartData
            {
                GlucoseData = [new GlucosePointDto { Time = 1, Sgv = 120 }],
                BolusMarkers = [new BolusMarkerDto()],
                CarbMarkers = [new CarbMarkerDto()],
                DeviceEventMarkers = [new DeviceEventMarkerDto()],
                TrackerMarkers = [new TrackerMarkerDto()],
            });

        var chart = await Service().ReadChartAsync(
            Face(new ClockElement
            {
                Type = "chart",
                ChartConfig = new ClockChartConfig { ShowBolus = true, ShowCarbs = false },
            }),
            AllScopes, CancellationToken.None);

        chart.Chart!.GlucoseData.Should().ContainSingle();
        chart.Chart.BolusMarkers.Should().ContainSingle();
        chart.Chart.CarbMarkers.Should().BeEmpty();
        chart.Chart.DeviceEventMarkers.Should().BeEmpty();
        chart.Chart.TrackerMarkers.Should().BeEmpty();
    }

    private static readonly HashSet<string> AllScopes =
        [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.AlertsRead];

    private static ClockFaceConfig Face(params ClockElement[] elements) => new()
    {
        Rows = [new ClockRow { Elements = [.. elements] }],
    };

    private TrackerInstance Running(Guid definitionId, string name, TrackerCategory category = TrackerCategory.Consumable) => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = _time.GetUtcNow().UtcDateTime.AddHours(-24),
        Definition = TrackerDefinition.Restore(definitionId, name, category: category, lifespanHours: 72),
    };
}
