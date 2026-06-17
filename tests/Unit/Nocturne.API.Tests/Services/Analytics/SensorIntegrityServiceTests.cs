using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Orchestration tests for <see cref="SensorIntegrityService"/>. The detector itself is covered by
/// golden-vector parity tests; these exercise the service's own logic: range sanitization, insulin
/// correlation, nocturnal classification, and the per-source breakdown.
/// </summary>
public class SensorIntegrityServiceTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HypoEventOptions LowConf = new() { MinConfidence = ClusterConfidence.Low };

    [Fact]
    public async Task Detects_cluster_and_hypo_event()
    {
        var readings = OscillationThenDescent(Base, source: "dexcom-connector");
        var service = BuildService(readings, []);

        var report = await service.AnalyzeAsync(Base.AddHours(-1), Base.AddHours(8), hypoOptions: LowConf);

        report.Clusters.Should().NotBeEmpty();
        report.HypoEvents.Should().NotBeEmpty();
        report.Summary.Clusters.Should().Be(report.Clusters.Count);
        report.Summary.Events.Should().Be(report.HypoEvents.Count);
    }

    [Fact]
    public async Task Drops_implausible_readings_before_detection()
    {
        var clean = OscillationThenDescent(Base, source: "dexcom-connector");

        // Same series with physiologically-impossible spikes interleaved. If these reached the
        // detector they would manufacture huge phantom reversals and change the cluster set.
        var withJunk = clean
            .SelectMany(r => new[]
            {
                r,
                new SensorGlucose { Timestamp = r.Timestamp.AddSeconds(1), Mgdl = 0, DataSource = r.DataSource },
                new SensorGlucose { Timestamp = r.Timestamp.AddSeconds(2), Mgdl = 700, DataSource = r.DataSource },
            })
            .ToList();

        var cleanReport = await BuildService(clean, []).AnalyzeAsync(
            Base.AddHours(-1), Base.AddHours(8), hypoOptions: LowConf);
        var junkReport = await BuildService(withJunk, []).AnalyzeAsync(
            Base.AddHours(-1), Base.AddHours(8), hypoOptions: LowConf);

        junkReport.Clusters.Should().HaveCount(cleanReport.Clusters.Count);
        junkReport.Clusters.Select(c => c.Confidence)
            .Should().Equal(cleanReport.Clusters.Select(c => c.Confidence));
    }

    [Fact]
    public async Task Classifies_nadir_as_nocturnal_using_utc_offset()
    {
        // UTC+11: a nadir at ~20:00 UTC is ~07:00... choose times so local lands at night.
        var readings = OscillationThenDescent(Base, source: "dexcom-connector", utcOffsetMinutes: 11 * 60);
        var service = BuildService(readings, []);

        var report = await service.AnalyzeAsync(Base.AddHours(-1), Base.AddHours(8), hypoOptions: LowConf);

        var ev = report.HypoEvents.Should().ContainSingle().Subject;
        var localHour = ev.Event.NadirTime.AddMinutes(11 * 60).Hour;
        ev.IsNocturnal.Should().Be(localHour >= 22 || localHour < 7);
        report.Summary.NocturnalEvents.Should().Be(report.HypoEvents.Count(e => e.IsNocturnal));
    }

    [Fact]
    public async Task RequireInsulin_filters_events_without_dosing()
    {
        var readings = OscillationThenDescent(Base, source: "dexcom-connector");

        var withoutInsulin = await BuildService(readings, []).AnalyzeAsync(
            Base.AddHours(-1), Base.AddHours(8),
            hypoOptions: new HypoEventOptions { MinConfidence = ClusterConfidence.Low, RequireInsulin = true });
        withoutInsulin.HypoEvents.Should().BeEmpty();

        // A bolus dosed during the cluster window makes the event qualify.
        var clusterTime = readings[5].Timestamp;
        var boluses = new List<Bolus> { new() { Timestamp = clusterTime, Insulin = 3.0 } };
        var withInsulin = await BuildService(readings, boluses).AnalyzeAsync(
            Base.AddHours(-1), Base.AddHours(8),
            hypoOptions: new HypoEventOptions { MinConfidence = ClusterConfidence.Low, RequireInsulin = true });

        withInsulin.HypoEvents.Should().NotBeEmpty();
        withInsulin.HypoEvents[0].Event.InsulinDuringCluster.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Per_source_breakdown_splits_by_data_source()
    {
        var dexcom = OscillationThenDescent(Base, source: "dexcom-connector");
        var libre = OscillationThenDescent(Base.AddDays(1), source: "libre-connector");
        var service = BuildService([.. dexcom, .. libre], []);

        var report = await service.AnalyzeAsync(
            Base.AddHours(-1), Base.AddDays(2), bySource: true, hypoOptions: LowConf);

        report.PerSource.Should().NotBeNull();
        report.PerSource!.Select(p => p.Source)
            .Should().BeEquivalentTo(["dexcom-connector", "libre-connector"]);
        // The two sources occupy disjoint days, so per-source totals sum to the combined totals.
        report.PerSource.Sum(p => p.Summary.Clusters).Should().Be(report.Summary.Clusters);
        report.PerSource.Sum(p => p.Summary.Events).Should().Be(report.Summary.Events);
        report.PerSource.Sum(p => p.Summary.NocturnalEvents).Should().Be(report.Summary.NocturnalEvents);
        report.PerSource.Should().OnlyContain(p => p.Summary.Events == 1);
    }

    [Fact]
    public async Task Classifies_nocturnal_via_utc_when_offset_is_missing()
    {
        // No per-reading offset → the nocturnal test falls back to the UTC hour. Anchor the series
        // so the nadir lands in the early-morning UTC window (< 07:00), i.e. nocturnal.
        var night = new DateTime(2026, 1, 1, 3, 0, 0, DateTimeKind.Utc);
        var readings = OscillationThenDescent(night, source: "dexcom-connector", utcOffsetMinutes: null);
        var service = BuildService(readings, []);

        var report = await service.AnalyzeAsync(night.AddHours(-1), night.AddHours(8), hypoOptions: LowConf);

        var ev = report.HypoEvents.Should().ContainSingle().Subject;
        ev.Event.NadirTime.Hour.Should().BeLessThan(7);
        ev.IsNocturnal.Should().BeTrue();
        report.Summary.NocturnalEvents.Should().Be(1);
    }

    [Fact]
    public async Task Empty_data_yields_empty_report()
    {
        var report = await BuildService([], []).AnalyzeAsync(Base, Base.AddHours(8), hypoOptions: LowConf);

        report.Clusters.Should().BeEmpty();
        report.HypoEvents.Should().BeEmpty();
        report.Summary.Days.Should().Be(0);
    }

    // 24-point 5-minute high-amplitude oscillation followed by a descent into hypoglycemia.
    // Mirrors the 'cluster_then_hypo' golden case: yields one cluster and one cluster-linked hypo.
    private static List<SensorGlucose> OscillationThenDescent(
        DateTime start, string source, int? utcOffsetMinutes = null)
    {
        var readings = new List<SensorGlucose>();
        for (var i = 0; i < 24; i++)
        {
            readings.Add(new SensorGlucose
            {
                Timestamp = start.AddMinutes(5 * i),
                Mgdl = 150 + (i % 2 == 0 ? 25 : -25),
                DataSource = source,
                UtcOffset = utcOffsetMinutes,
            });
        }

        var tailStart = start.AddMinutes(5 * 24);
        for (var i = 0; i < 20; i++)
        {
            readings.Add(new SensorGlucose
            {
                Timestamp = tailStart.AddMinutes(5 * i),
                Mgdl = Math.Max(55, 150 - (6 * i)),
                DataSource = source,
                UtcOffset = utcOffsetMinutes,
            });
        }

        return readings;
    }

    private static SensorIntegrityService BuildService(
        List<SensorGlucose> readings, List<Bolus> boluses)
    {
        var glucoseRepo = new Mock<ISensorGlucoseRepository>();
        glucoseRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime? _, DateTime? _, string? _, string? source, int _, int _, bool _, bool _,
                DateTime? _, Guid? _, CancellationToken _) =>
                source is null ? readings : readings.Where(r => r.DataSource == source).ToList());

        var bolusRepo = new Mock<IBolusRepository>();
        bolusRepo
            .Setup(r => r.GetAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<BolusKind?>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(boluses);

        return new SensorIntegrityService(glucoseRepo.Object, bolusRepo.Object);
    }
}
