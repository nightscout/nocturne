using FluentAssertions;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.Analytics;

public class CgmComparisonCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SensorGlucose Reading(double minutes, double mgdl) =>
        new() { Timestamp = T0.AddMinutes(minutes), Mgdl = mgdl };

    private static CgmPairedReading Pair(double mgdlA, double mgdlB) =>
        new() { MgdlA = mgdlA, MgdlB = mgdlB };

    [Fact]
    public void Compare_pairs_each_a_reading_with_the_nearest_b_reading()
    {
        var a = new[] { Reading(0, 100), Reading(5, 110), Reading(10, 120) };
        var b = new[] { Reading(1, 104), Reading(6, 114), Reading(11, 124) };

        var result = CgmComparisonCalculator.Compare(a, b, TimeSpan.FromMinutes(5));

        result.Pairs.Should().HaveCount(3);
        result.Pairs.Select(p => p.MgdlB).Should().Equal(104, 114, 124);
        result.Pairs.Select(p => p.TimestampB).Should().Equal(
            T0.AddMinutes(1), T0.AddMinutes(6), T0.AddMinutes(11));
        result.UnpairedCountA.Should().Be(0);
        result.UnpairedCountB.Should().Be(0);
        result.ReadingCountA.Should().Be(3);
        result.ReadingCountB.Should().Be(3);
        result.ToleranceMinutes.Should().Be(5);
    }

    [Fact]
    public void Compare_prefers_the_closer_candidate_on_either_side()
    {
        var a = new[] { Reading(10, 100) };
        var b = new[] { Reading(7, 90), Reading(11, 95) };

        var result = CgmComparisonCalculator.Compare(a, b, TimeSpan.FromMinutes(5));

        result.Pairs.Should().ContainSingle().Which.MgdlB.Should().Be(95);
        result.UnpairedCountB.Should().Be(1);
    }

    [Fact]
    public void Compare_breaks_an_equidistant_tie_towards_the_earlier_b_reading()
    {
        var a = new[] { Reading(10, 100) };
        var b = new[] { Reading(8, 90), Reading(12, 95) };

        var result = CgmComparisonCalculator.Compare(a, b, TimeSpan.FromMinutes(5));

        result.Pairs.Should().ContainSingle().Which.TimestampB.Should().Be(T0.AddMinutes(8));
    }

    [Fact]
    public void Compare_matches_at_the_tolerance_boundary_but_not_beyond_it()
    {
        var a = new[] { Reading(0, 100), Reading(60, 100) };
        var b = new[] { Reading(5, 90), Reading(65.001, 90) };

        var result = CgmComparisonCalculator.Compare(a, b, TimeSpan.FromMinutes(5));

        result.Pairs.Should().ContainSingle().Which.TimestampA.Should().Be(T0);
        result.UnpairedCountA.Should().Be(1);
        result.UnpairedCountB.Should().Be(1);
    }

    [Fact]
    public void Compare_counts_a_shared_b_reading_once_and_leaves_no_unpaired_b()
    {
        var a = new[] { Reading(0, 100), Reading(1, 101) };
        var b = new[] { Reading(0.5, 90) };

        var result = CgmComparisonCalculator.Compare(a, b, TimeSpan.FromMinutes(5));

        result.Pairs.Should().HaveCount(2);
        result.UnpairedCountA.Should().Be(0);
        result.UnpairedCountB.Should().Be(0);
    }

    [Fact]
    public void Compare_drops_non_positive_values_before_pairing()
    {
        var a = new[] { Reading(0, 0), Reading(5, 110) };
        var b = new[] { Reading(0, -5), Reading(5, 100) };

        var result = CgmComparisonCalculator.Compare(a, b, TimeSpan.FromMinutes(5));

        result.ReadingCountA.Should().Be(1);
        result.ReadingCountB.Should().Be(1);
        result.Pairs.Should().ContainSingle().Which.MgdlA.Should().Be(110);
    }

    [Fact]
    public void Compare_returns_every_a_reading_unpaired_when_b_has_no_readings()
    {
        var result = CgmComparisonCalculator.Compare(
            [Reading(0, 100), Reading(5, 110)], [], TimeSpan.FromMinutes(5));

        result.Pairs.Should().BeEmpty();
        result.UnpairedCountA.Should().Be(2);
        result.UnpairedCountB.Should().Be(0);
        result.Metrics.Should().BeNull();
    }

    [Fact]
    public void Measure_computes_the_hand_worked_figures()
    {
        // |A-B| = 10, 20, 6; A-B = +10, -20, +6
        var pairs = new[] { Pair(110, 100), Pair(180, 200), Pair(306, 300) };

        var metrics = CgmComparisonCalculator.Measure(pairs)!;

        metrics.PairCount.Should().Be(3);
        metrics.MeanAbsoluteDifferenceMgdl.Should().BeApproximately(12, 1e-9);
        metrics.BiasMgdl.Should().BeApproximately(-4.0 / 3.0, 1e-9);
        // (10/100 + 20/200 + 6/300) / 3 * 100
        metrics.MardPercent.Should().BeApproximately(220.0 / 30.0, 1e-9);
    }

    [Fact]
    public void Measure_takes_device_b_as_the_relative_reference()
    {
        var forward = CgmComparisonCalculator.Measure([Pair(150, 100)])!;
        var reversed = CgmComparisonCalculator.Measure([Pair(100, 150)])!;

        forward.MardPercent.Should().BeApproximately(50, 1e-9);
        reversed.MardPercent.Should().BeApproximately(100.0 / 3.0, 1e-9);
        forward.BiasMgdl.Should().Be(50);
        reversed.BiasMgdl.Should().Be(-50);
    }

    [Theory]
    // Reference below 100 mg/dL: absolute 15 mg/dL band, inclusive.
    [InlineData(84, 99, true)]
    [InlineData(83.99, 99, false)]
    [InlineData(114, 99, true)]
    [InlineData(114.01, 99, false)]
    // Reference at or above 100 mg/dL: relative 15% band, inclusive. At exactly 100 the two
    // rules coincide, so the switch itself is only observable above it.
    [InlineData(85, 100, true)]
    [InlineData(84.99, 100, false)]
    [InlineData(170, 200, true)]
    [InlineData(169.99, 200, false)]
    // A 15 mg/dL miss against a 200 mg/dL reference passes only because the relative rule applies.
    [InlineData(215, 200, true)]
    public void Measure_switches_the_concordance_rule_at_100_mgdl(double mgdlA, double mgdlB, bool within)
    {
        var metrics = CgmComparisonCalculator.Measure([Pair(mgdlA, mgdlB)])!;

        metrics.Within15Percent.Should().Be(within ? 100 : 0);
    }

    [Fact]
    public void Measure_reports_the_within_15_share_of_the_series()
    {
        var pairs = new[] { Pair(100, 100), Pair(200, 100), Pair(90, 100), Pair(300, 100) };

        var metrics = CgmComparisonCalculator.Measure(pairs)!;

        metrics.Within15Percent.Should().Be(50);
    }

    [Fact]
    public void Measure_returns_null_for_an_empty_series()
    {
        CgmComparisonCalculator.Measure([]).Should().BeNull();
    }
}
