using FluentAssertions;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Core.Models.Tests.Timezones;

[Trait("Category", "Unit")]
public class DeviceClockEstimatorTests
{
    private static DateTime Utc(int d, int h, int mi = 0, int s = 0) =>
        new(2026, 4, d, h, mi, s, DateTimeKind.Utc);

    // ── Lower bounds ─────────────────────────────────────────────────────────

    [Fact]
    public void SparseBatch_ProducesLowerBound_FromNewestRecord()
    {
        // Device at UTC+2 (wall = UTC+2h); two boluses uploaded together 30 min after the newest.
        var sync = Utc(10, 12, 0);
        var obs = DeviceClockEstimator.FromUploadBatches("glooko",
        [
            (Utc(10, 13, 30), sync), // wall 13:30 = 11:30 UTC → recorded 30 min before upload
            (Utc(10, 9, 0), sync),
        ]);

        var single = obs.Should().ContainSingle().Subject;
        single.IsEstimate.Should().BeFalse();
        single.OffsetMinutes.Should().Be(90); // offset(120) − lag(30): a hard lower bound
        single.SampleCount.Should().Be(2);
        single.ObservedAtUtc.Should().Be(sync);
        single.Source.Should().Be(DeviceClockObservationSource.UploadBatch);
    }

    [Fact]
    public void CoversFrom_IsOldestRecordConvertedWithTheObservationsOwnOffset()
    {
        var sync = Utc(10, 12, 0);
        var obs = DeviceClockEstimator.FromUploadBatches("glooko",
        [
            (Utc(10, 13, 30), sync),
            (Utc(10, 9, 0), sync),
        ]);

        obs[0].CoversFromUtc.Should().Be(Utc(10, 9, 0).AddMinutes(-90));
    }

    // ── Dense prompt uploads become two-sided estimates ──────────────────────

    [Fact]
    public void DenseEvenlySpacedBatch_BecomesTwoSidedEstimate_WithSamplingIntervalAdded()
    {
        // 8 CGM readings 5 min apart, newest created right at upload (device UTC+2, no lag).
        var sync = Utc(10, 12, 0);
        var samples = Enumerable.Range(0, 8)
            .Select(i => (Utc(10, 14, 0).AddMinutes(-5 * i), sync));

        var obs = DeviceClockEstimator.FromUploadBatches("glooko", samples);

        var single = obs.Should().ContainSingle().Subject;
        single.IsEstimate.Should().BeTrue();
        single.OffsetMinutes.Should().Be(125); // bound(120) + spacing(5)
        single.SampleCount.Should().Be(8);
    }

    [Fact]
    public void DenseButIrregularBatch_StaysABound()
    {
        // 6 records but 20-minute median spacing: not a prompt CGM stream.
        var sync = Utc(10, 12, 0);
        var samples = Enumerable.Range(0, 6)
            .Select(i => (Utc(10, 14, 0).AddMinutes(-20 * i), sync));

        var obs = DeviceClockEstimator.FromUploadBatches("glooko", samples);

        obs.Should().ContainSingle().Which.IsEstimate.Should().BeFalse();
    }

    [Fact]
    public void SmallBatch_NeverBecomesAnEstimate()
    {
        var sync = Utc(10, 12, 0);
        var samples = Enumerable.Range(0, DeviceClockEstimator.DenseMinSamples - 1)
            .Select(i => (Utc(10, 14, 0).AddMinutes(-5 * i), sync));

        DeviceClockEstimator.FromUploadBatches("glooko", samples)
            .Should().ContainSingle().Which.IsEstimate.Should().BeFalse();
    }

    // ── Batching ─────────────────────────────────────────────────────────────

    [Fact]
    public void RecordsGroupByUploadTimestamp_OneObservationPerBatch_OrderedByTime()
    {
        var obs = DeviceClockEstimator.FromUploadBatches("glooko",
        [
            (Utc(11, 10, 0), Utc(11, 9, 0)),
            (Utc(10, 10, 0), Utc(10, 9, 0)),
            (Utc(10, 10, 5), Utc(10, 9, 0)),
        ]);

        obs.Should().HaveCount(2);
        obs[0].ObservedAtUtc.Should().Be(Utc(10, 9, 0));
        obs[0].SampleCount.Should().Be(2);
        obs[1].ObservedAtUtc.Should().Be(Utc(11, 9, 0));
    }

    [Fact]
    public void ImplausibleOffset_IsDroppedAsCorrupt()
    {
        // A record "recorded" 20 hours after its own upload cannot be a device clock.
        var obs = DeviceClockEstimator.FromUploadBatches("glooko",
        [
            (Utc(11, 12, 0), Utc(10, 16, 0)),
        ]);

        obs.Should().BeEmpty();
    }

    [Fact]
    public void WestOfUtcDevice_ProducesNegativeBound()
    {
        // Device at UTC−4: wall clock behind real UTC.
        var sync = Utc(10, 12, 0);
        var obs = DeviceClockEstimator.FromUploadBatches("glooko",
        [
            (Utc(10, 7, 30), sync), // −4h offset − 30 min lag = −270
        ]);

        obs.Should().ContainSingle().Which.OffsetMinutes.Should().Be(-270);
    }
}
