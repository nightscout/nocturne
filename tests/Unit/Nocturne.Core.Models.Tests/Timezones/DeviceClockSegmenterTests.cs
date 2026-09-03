using FluentAssertions;
using Nocturne.Core.Models.Timezones;
using Xunit;

namespace Nocturne.Core.Models.Tests.Timezones;

[Trait("Category", "Unit")]
public class DeviceClockSegmenterTests
{
    private const int HomeOffset = -240; // America/New_York in April (EDT −4h)

    private static int Expected(DateTime _) => HomeOffset;

    private static DateTime Utc(int d, int h, int mi = 0) => new(2026, 4, d, h, mi, 0, DateTimeKind.Utc);

    private static DeviceClockObservation Obs(
        DateTime at, int offset, bool estimate = true, DateTime? coversFrom = null) => new()
    {
        Connector = "glooko",
        Source = DeviceClockObservationSource.UploadBatch,
        ObservedAtUtc = at,
        OffsetMinutes = offset,
        IsEstimate = estimate,
        SampleCount = 5,
        CoversFromUtc = coversFrom,
    };

    // ── The gates ────────────────────────────────────────────────────────────

    [Fact]
    public void SingleAnomalousObservation_NeverProducesASegment()
    {
        var segments = DeviceClockSegmenter.Derive([Obs(Utc(10, 12), 120)], Expected);
        segments.Should().BeEmpty();
    }

    [Fact]
    public void TwoObservations_StillBelowTheConsecutiveGate()
    {
        var segments = DeviceClockSegmenter.Derive(
            [Obs(Utc(10, 12), 120), Obs(Utc(10, 18), 120)], Expected);
        segments.Should().BeEmpty();
    }

    [Fact]
    public void DeviationOfExactlyThirtyMinutes_IsATimezone()
    {
        // Half-hour zones are the smallest real step the gate exists to catch; the boundary is
        // inclusive for estimates and bounds alike.
        DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), HomeOffset + DeviceClockSegmenter.MinDeviationMinutes),
                Obs(Utc(10, 18), HomeOffset + DeviceClockSegmenter.MinDeviationMinutes),
                Obs(Utc(11, 6), HomeOffset + DeviceClockSegmenter.MinDeviationMinutes),
            ],
            Expected)
            .Should().ContainSingle().Which.OffsetMinutes.Should().Be(HomeOffset + 30);

        DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), HomeOffset + DeviceClockSegmenter.MinDeviationMinutes, estimate: false),
                Obs(Utc(10, 18), HomeOffset + DeviceClockSegmenter.MinDeviationMinutes, estimate: false),
                Obs(Utc(11, 6), HomeOffset + DeviceClockSegmenter.MinDeviationMinutes, estimate: false),
            ],
            Expected)
            .Should().ContainSingle();
    }

    [Fact]
    public void DeviationUnderThirtyMinutes_IsDriftNotATimezone()
    {
        // 25 minutes fast, persistently: still no segment.
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), HomeOffset + 25),
                Obs(Utc(10, 18), HomeOffset + 25),
                Obs(Utc(11, 12), HomeOffset + 25),
                Obs(Utc(11, 18), HomeOffset + 25),
            ],
            Expected);

        segments.Should().BeEmpty();
    }

    [Fact]
    public void ThreeAgreeingDeviantEstimates_ConfirmASegment()
    {
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), 118),
                Obs(Utc(10, 18), 122),
                Obs(Utc(11, 6), 120),
            ],
            Expected);

        var segment = segments.Should().ContainSingle().Subject;
        segment.OffsetMinutes.Should().Be(120); // median snapped to 15-minute granularity
        segment.ObservationCount.Should().Be(3);
        segment.ToUtc.Should().BeNull(); // nothing later refutes it → ongoing
    }

    [Fact]
    public void DisagreeingEstimates_ErraticUploads_StaySilent()
    {
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), 120),
                Obs(Utc(10, 18), 200),
                Obs(Utc(11, 6), 60),
                Obs(Utc(11, 12), 150),
            ],
            Expected);

        segments.Should().BeEmpty();
    }

    // ── Bounds are one-sided ─────────────────────────────────────────────────

    [Fact]
    public void DeviantBounds_ConfirmASegment_AtTheTightestBound()
    {
        // Poseidon-shaped: bolus-only bounds with varying lag, all proving the clock ran ahead.
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), -180, estimate: false),
                Obs(Utc(10, 20), 25, estimate: false), // tightest: offset ≥ +25
                Obs(Utc(11, 9), -100, estimate: false),
            ],
            Expected);

        var segment = segments.Should().ContainSingle().Subject;
        segment.OffsetMinutes.Should().Be(30); // max bound (25) snapped to granularity
        segment.ObservationCount.Should().Be(3);
    }

    [Fact]
    public void LowBound_IsNeutral_DoesNotBreakARun()
    {
        // A big-lag bound sits below the deviation threshold mid-run; the run must survive it.
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), 120),
                Obs(Utc(10, 18), -260, estimate: false), // neutral: compatible with both states
                Obs(Utc(11, 6), 120),
                Obs(Utc(11, 12), 120),
            ],
            Expected);

        segments.Should().ContainSingle().Which.ObservationCount.Should().Be(3);
    }

    [Fact]
    public void WestwardTravel_IsInvisibleToBoundsAlone()
    {
        // Device at UTC−7 while home is −4: every bound is below expected, so nothing is provable.
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), -420, estimate: false),
                Obs(Utc(10, 18), -430, estimate: false),
                Obs(Utc(11, 6), -425, estimate: false),
            ],
            Expected);

        segments.Should().BeEmpty();
    }

    [Fact]
    public void WestwardTravel_IsDetectedByEstimates()
    {
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), -420),
                Obs(Utc(10, 18), -420),
                Obs(Utc(11, 6), -420),
            ],
            Expected);

        segments.Should().ContainSingle().Which.OffsetMinutes.Should().Be(-420);
    }

    // ── Run boundaries ───────────────────────────────────────────────────────

    [Fact]
    public void RefutingEstimate_ClosesTheRun_AtItsLastSupportingObservation()
    {
        var lastSupporting = Utc(12, 6);
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), 120),
                Obs(Utc(11, 12), 120),
                Obs(lastSupporting, 120),
                Obs(Utc(13, 12), HomeOffset), // back home
            ],
            Expected);

        var segment = segments.Should().ContainSingle().Subject;
        segment.ToUtc.Should().Be(lastSupporting);
    }

    [Fact]
    public void LongSilence_SplitsRuns_SoSparseEvidenceCannotBridgeTwoTrips()
    {
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(1, 12), 120),
                Obs(Utc(1, 18), 120),
                Obs(Utc(2, 6), 120),
                // > 48h of nothing
                Obs(Utc(20, 12), 120),
                Obs(Utc(20, 18), 120),
                Obs(Utc(21, 6), 120),
            ],
            Expected);

        segments.Should().HaveCount(2);
        segments[0].ToUtc.Should().Be(Utc(2, 6));
        segments[1].ToUtc.Should().BeNull();
    }

    [Fact]
    public void SegmentStart_ComesFromCoversFrom_FlooredByPrecedingEvidence()
    {
        var floor = Utc(9, 23);
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(floor, HomeOffset), // normal evidence just before the trip
                // First deviant batch was a backlog flush whose oldest record predates the trip.
                Obs(Utc(10, 12), 120, coversFrom: Utc(8, 0)),
                Obs(Utc(10, 18), 120, coversFrom: Utc(10, 11)),
                Obs(Utc(11, 6), 120, coversFrom: Utc(11, 0)),
            ],
            Expected);

        segments.Should().ContainSingle().Which.FromUtc.Should().Be(floor);
    }

    [Fact]
    public void SegmentStart_CannotReachFurtherBack_ThanTheLookbackCapAllows()
    {
        // The floor is a week stale (device offline); the first deviant batch is a backlog flush
        // whose CoversFrom reaches deep into the offline window. The cap bounds the contamination.
        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(1, 0), HomeOffset), // stale floor, 9+ days before the run
                Obs(Utc(10, 12), 120, coversFrom: Utc(3, 0)),
                Obs(Utc(10, 18), 120),
                Obs(Utc(11, 6), 120),
            ],
            Expected);

        segments.Should().ContainSingle().Which.FromUtc
            .Should().Be(Utc(10, 12).AddHours(-DeviceClockSegmenter.MaxCoversLookbackHours));
    }

    [Fact]
    public void SegmentIdentity_IsItsFirstSupportingObservation()
    {
        var first = Utc(10, 12);
        var segments = DeviceClockSegmenter.Derive(
            [Obs(first, 120), Obs(Utc(10, 18), 120), Obs(Utc(11, 6), 120)], Expected);

        segments.Should().ContainSingle().Which.FirstObservedAtUtc.Should().Be(first);
    }

    [Fact]
    public void SegmentContains_IncludesItsStart_AndExcludesItsEnd()
    {
        var segment = new DeviceClockSegment { FromUtc = Utc(10, 0), ToUtc = Utc(13, 0), OffsetMinutes = 120 };

        segment.Contains(Utc(10, 0)).Should().BeTrue("the start boundary is part of the deviation");
        segment.Contains(Utc(10, 0).AddTicks(-1)).Should().BeFalse();
        segment.Contains(Utc(13, 0)).Should().BeFalse("the end boundary belongs to the next regime");
        segment.Contains(Utc(13, 0).AddTicks(-1)).Should().BeTrue();

        var open = new DeviceClockSegment { FromUtc = Utc(10, 0), ToUtc = null, OffsetMinutes = 120 };
        open.Contains(Utc(30, 0)).Should().BeTrue();
    }

    [Fact]
    public void DstTransition_DoesNotSplitASegment_WhenTheDeviceOffsetIsConstant()
    {
        // Expected offset changes by an hour mid-run (zone left DST); the device clock did not move.
        var transition = Utc(11, 0);
        int DstExpected(DateTime t) => t < transition ? HomeOffset : HomeOffset - 60;

        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), 120),
                Obs(Utc(10, 18), 120),
                Obs(Utc(11, 12), 120),
                Obs(Utc(11, 18), 120),
            ],
            DstExpected);

        segments.Should().ContainSingle().Which.ObservationCount.Should().Be(4);
    }

    [Fact]
    public void DeviceFollowingItsZoneThroughDst_NeverDeviates()
    {
        var transition = Utc(11, 0);
        int DstExpected(DateTime t) => t < transition ? HomeOffset : HomeOffset - 60;

        var segments = DeviceClockSegmenter.Derive(
            [
                Obs(Utc(10, 12), HomeOffset),
                Obs(Utc(10, 18), HomeOffset),
                Obs(Utc(11, 12), HomeOffset - 60),
                Obs(Utc(11, 18), HomeOffset - 60),
            ],
            DstExpected);

        segments.Should().BeEmpty();
    }
}
