using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Analytics;
using Xunit;

namespace Nocturne.Core.Models.Tests.Analytics.SensorIntegrity;

/// <summary>
/// Asserts the C# <see cref="SensorIntegrityDetector"/> reproduces, field-for-field, the output of
/// the reference Python detector (cgm_cluster_detector_v5, release v6.0) over a battery of crafted
/// edge cases. The fixture <c>sensor_integrity_golden.json</c> and its generator
/// <c>generate_golden.py</c> live alongside this test; regenerate with
/// <c>python generate_golden.py</c> after any intentional algorithm change.
/// </summary>
public class SensorIntegrityDetectorGoldenTests
{
    private const double Tol = 1e-5;

    private static readonly JsonElement Fixture = LoadFixture();

    public static IEnumerable<object[]> Cases()
    {
        foreach (var c in Fixture.GetProperty("cases").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("name").GetString()!, c };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Detector_matches_reference(string name, JsonElement c)
    {
        _ = name; // surfaced as the test case label

        var (timestamps, glucose) = ReadSeries(c.GetProperty("input"));

        var clusters = SensorIntegrityDetector.DetectClusters(timestamps, glucose);

        var expectedClusters = c.GetProperty("expected_clusters").EnumerateArray().ToList();
        clusters.Should().HaveCount(expectedClusters.Count);

        for (var i = 0; i < expectedClusters.Count; i++)
        {
            AssertCluster(clusters[i], expectedClusters[i]);
        }

        if (!c.TryGetProperty("expected_events", out var expectedEventsEl))
        {
            return;
        }

        var options = ReadHypoOptions(c.GetProperty("hypo_kwargs"));
        var insulin = ReadInsulin(c.GetProperty("input"));

        var events = SensorIntegrityDetector.FindHypoEvents(
            timestamps, glucose, options, insulin: insulin);

        var expectedEvents = expectedEventsEl.EnumerateArray().ToList();
        events.Should().HaveCount(expectedEvents.Count);

        for (var i = 0; i < expectedEvents.Count; i++)
        {
            AssertEvent(events[i], expectedEvents[i]);
        }
    }

    private static void AssertCluster(GlucoseCluster actual, JsonElement expected)
    {
        actual.Start.Should().Be(ParseTime(expected.GetProperty("start")));
        actual.End.Should().Be(ParseTime(expected.GetProperty("end")));
        actual.MinMgdl.Should().BeApproximately(expected.GetProperty("min").GetDouble(), Tol);
        actual.MaxMgdl.Should().BeApproximately(expected.GetProperty("max").GetDouble(), Tol);
        actual.DurationMinutes.Should().BeApproximately(expected.GetProperty("duration_min").GetDouble(), Tol);
        actual.Confidence.Should().Be(ParseConfidence(expected.GetProperty("confidence").GetString()!));

        var debug = expected.GetProperty("debug");
        var diag = actual.Diagnostics;
        diag.SamplingIntervalMinutes.Should().BeApproximately(debug.GetProperty("dt_min").GetDouble(), Tol);
        diag.WindowPoints.Should().Be(debug.GetProperty("win_pts").GetInt32());
        diag.PeakReversals.Should().BeApproximately(debug.GetProperty("peak_reversals_window").GetDouble(), Tol);
        diag.PeakIncoherenceRatio.Should().BeApproximately(debug.GetProperty("peak_incoh_ratio").GetDouble(), Tol);
        diag.Amplitude.Should().BeApproximately(debug.GetProperty("amp_val").GetDouble(), Tol);
        diag.MaxStep.Should().BeApproximately(debug.GetProperty("step_max").GetDouble(), Tol);
        diag.SpikePromoted.Should().Be(debug.GetProperty("bumped").GetBoolean());
        diag.ChainSize.Should().Be(debug.GetProperty("chain_size").GetInt32());
        diag.ChainPromoted.Should().Be(debug.GetProperty("chain_promoted").GetBoolean());
    }

    private static void AssertEvent(HypoEvent actual, JsonElement expected)
    {
        actual.Cluster.Start.Should().Be(ParseTime(expected.GetProperty("cluster_start")));
        actual.Cluster.End.Should().Be(ParseTime(expected.GetProperty("cluster_end")));
        actual.Cluster.Confidence.Should().Be(ParseConfidence(expected.GetProperty("cluster_confidence").GetString()!));
        actual.NadirMgdl.Should().BeApproximately(expected.GetProperty("nadir").GetDouble(), Tol);
        actual.NadirTime.Should().Be(ParseTime(expected.GetProperty("nadir_time")));
        actual.TimeToNadirHours.Should().BeApproximately(expected.GetProperty("time_to_nadir_hours").GetDouble(), Tol);
        actual.ReadingsBelowThreshold.Should().Be(expected.GetProperty("n_readings_below_threshold").GetInt32());

        var expectedDoses = expected.GetProperty("insulin_during_cluster").EnumerateArray().ToList();
        actual.InsulinDuringCluster.Should().HaveCount(expectedDoses.Count);
        for (var i = 0; i < expectedDoses.Count; i++)
        {
            actual.InsulinDuringCluster[i].Time.Should().Be(ParseTime(expectedDoses[i].GetProperty("time")));
            actual.InsulinDuringCluster[i].Units.Should()
                .BeApproximately(expectedDoses[i].GetProperty("units").GetDouble(), Tol);
        }
    }

    private static (List<DateTime>, List<double>) ReadSeries(JsonElement input)
    {
        var ts = input.GetProperty("timestamps").EnumerateArray().Select(ParseTime).ToList();
        var g = input.GetProperty("glucose").EnumerateArray().Select(e => e.GetDouble()).ToList();
        return (ts, g);
    }

    private static List<InsulinDose>? ReadInsulin(JsonElement input)
    {
        if (!input.TryGetProperty("insulin_times", out var timesEl))
        {
            return null;
        }

        var times = timesEl.EnumerateArray().Select(ParseTime).ToList();
        var units = input.GetProperty("insulin_units").EnumerateArray().Select(e => e.GetDouble()).ToList();
        return times.Zip(units, (t, u) => new InsulinDose { Time = t, Units = u }).ToList();
    }

    private static HypoEventOptions ReadHypoOptions(JsonElement kwargs)
    {
        var minConf = kwargs.TryGetProperty("min_confidence", out var mc)
            ? ParseConfidence(mc.GetString()!)
            : ClusterConfidence.Medium;
        var requireInsulin = kwargs.TryGetProperty("require_insulin", out var ri) && ri.GetBoolean();
        return new HypoEventOptions { MinConfidence = minConf, RequireInsulin = requireInsulin };
    }

    private static DateTime ParseTime(JsonElement e) => DateTime.ParseExact(
        e.GetString()!, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static ClusterConfidence ParseConfidence(string s) => s switch
    {
        "low" => ClusterConfidence.Low,
        "medium" => ClusterConfidence.Medium,
        "high" => ClusterConfidence.High,
        _ => throw new ArgumentException($"Unknown confidence '{s}'"),
    };

    private static JsonElement LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Analytics", "SensorIntegrity", "sensor_integrity_golden.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }
}
