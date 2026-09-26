using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class StateSpanMetadataExtensionsTests
{
    // ----- TryReadDecimal -----

    [Fact]
    public void TryReadDecimal_returns_null_when_metadata_is_null()
    {
        Dictionary<string, object>? metadata = null;
        metadata.TryReadDecimal("k").Should().BeNull();
    }

    [Fact]
    public void TryReadDecimal_returns_null_when_key_missing()
    {
        var metadata = new Dictionary<string, object> { ["other"] = 1.0 };
        metadata.TryReadDecimal("missing").Should().BeNull();
    }

    [Fact]
    public void TryReadDecimal_round_trips_decimal()
    {
        var metadata = new Dictionary<string, object> { ["k"] = 1.25m };
        metadata.TryReadDecimal("k").Should().Be(1.25m);
    }

    [Fact]
    public void TryReadDecimal_round_trips_finite_double()
    {
        var metadata = new Dictionary<string, object> { ["k"] = 1.5 };
        metadata.TryReadDecimal("k").Should().Be(1.5m);
    }

    [Fact]
    public void TryReadDecimal_returns_null_for_double_positive_infinity()
    {
        var metadata = new Dictionary<string, object> { ["k"] = double.PositiveInfinity };
        metadata.TryReadDecimal("k").Should().BeNull();
    }

    [Fact]
    public void TryReadDecimal_returns_null_for_double_nan()
    {
        var metadata = new Dictionary<string, object> { ["k"] = double.NaN };
        metadata.TryReadDecimal("k").Should().BeNull();
    }

    [Fact]
    public void TryReadDecimal_parses_int_to_decimal()
    {
        var metadata = new Dictionary<string, object> { ["k"] = 7 };
        metadata.TryReadDecimal("k").Should().Be(7m);
    }

    [Fact]
    public void TryReadDecimal_parses_long_to_decimal()
    {
        var metadata = new Dictionary<string, object> { ["k"] = 42L };
        metadata.TryReadDecimal("k").Should().Be(42m);
    }

    [Fact]
    public void TryReadDecimal_parses_numeric_string_with_invariant_culture()
    {
        var metadata = new Dictionary<string, object> { ["k"] = "1.25" };
        metadata.TryReadDecimal("k").Should().Be(1.25m);
    }

    [Fact]
    public void TryReadDecimal_returns_null_for_unparseable_string()
    {
        var metadata = new Dictionary<string, object> { ["k"] = "not-a-number" };
        metadata.TryReadDecimal("k").Should().BeNull();
    }

    [Fact]
    public void TryReadDecimal_reads_jsonelement_number()
    {
        var je = JsonDocument.Parse("""{"k":1.5}""").RootElement.GetProperty("k");
        var metadata = new Dictionary<string, object> { ["k"] = je };
        metadata.TryReadDecimal("k").Should().Be(1.5m);
    }

    [Fact]
    public void TryReadDecimal_reads_jsonelement_numeric_string()
    {
        var je = JsonDocument.Parse("""{"k":"1.75"}""").RootElement.GetProperty("k");
        var metadata = new Dictionary<string, object> { ["k"] = je };
        metadata.TryReadDecimal("k").Should().Be(1.75m);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    public void TryReadDecimal_returns_null_for_jsonelement_non_numeric_kinds(string raw)
    {
        var je = JsonDocument.Parse($$"""{"k":{{raw}}}""").RootElement.GetProperty("k");
        var metadata = new Dictionary<string, object> { ["k"] = je };
        metadata.TryReadDecimal("k").Should().BeNull();
    }

    [Fact]
    public void TryReadDecimal_returns_null_for_bool()
    {
        var metadata = new Dictionary<string, object> { ["k"] = true };
        metadata.TryReadDecimal("k").Should().BeNull();
    }

    // ----- TryReadString -----

    [Fact]
    public void TryReadString_returns_plain_string()
    {
        var metadata = new Dictionary<string, object> { ["k"] = "hello" };
        metadata.TryReadString("k").Should().Be("hello");
    }

    [Fact]
    public void TryReadString_returns_null_when_value_not_string()
    {
        var metadata = new Dictionary<string, object> { ["k"] = 1.5 };
        metadata.TryReadString("k").Should().BeNull();
    }

    [Fact]
    public void TryReadString_reads_jsonelement_string()
    {
        var je = JsonDocument.Parse("""{"k":"world"}""").RootElement.GetProperty("k");
        var metadata = new Dictionary<string, object> { ["k"] = je };
        metadata.TryReadString("k").Should().Be("world");
    }

    [Fact]
    public void TryReadString_returns_null_for_jsonelement_number()
    {
        var je = JsonDocument.Parse("""{"k":42}""").RootElement.GetProperty("k");
        var metadata = new Dictionary<string, object> { ["k"] = je };
        metadata.TryReadString("k").Should().BeNull();
    }

    [Fact]
    public void TryReadString_returns_null_when_metadata_is_null()
    {
        Dictionary<string, object>? metadata = null;
        metadata.TryReadString("k").Should().BeNull();
    }

    [Fact]
    public void TryReadString_returns_null_when_key_missing()
    {
        var metadata = new Dictionary<string, object> { ["other"] = "x" };
        metadata.TryReadString("missing").Should().BeNull();
    }

    private static Dictionary<string, object> Treatment(string? reason, double? factor = 0.8)
    {
        var metadata = new Dictionary<string, object>
        {
            [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.TreatmentsCollection,
        };
        if (reason is not null) metadata["reason"] = reason;
        if (factor is not null) metadata["insulinNeedsScaleFactor"] = factor.Value;
        return metadata;
    }

    private static Dictionary<string, object> Snapshot(string? name, double? multiplier = 0.8)
    {
        var metadata = new Dictionary<string, object>
        {
            [StateSpanMetadataExtensions.CollectionKey] = StateSpanMetadataExtensions.DeviceStatusCollection,
        };
        if (name is not null) metadata["name"] = name;
        if (multiplier is not null) metadata["multiplier"] = multiplier.Value;
        return metadata;
    }

    [Fact]
    public void IsSameOverrideAs_does_not_match_a_record_without_a_collection()
    {
        var unmarked = Treatment("N Night");
        unmarked.Remove(StateSpanMetadataExtensions.CollectionKey);

        unmarked.IsSameOverrideAs(Snapshot("Night")).Should().BeFalse();
        Snapshot("Night").IsSameOverrideAs(unmarked).Should().BeFalse();
    }

    [Theory]
    [InlineData("N Night", "Night")]
    [InlineData("AB Night", "Night")]
    [InlineData("Pre-Meal", "Pre-Meal")]
    [InlineData("Vor dem Essen", "Vor dem Essen")]
    public void IsSameOverrideAs_matches_a_treatment_reason_to_its_snapshot_name(string reason, string name)
    {
        Treatment(reason).IsSameOverrideAs(Snapshot(name)).Should().BeTrue();
        Snapshot(name).IsSameOverrideAs(Treatment(reason)).Should().BeTrue();
    }

    [Fact]
    public void IsSameOverrideAs_ignores_the_scale_factor_of_a_named_override()
    {
        Treatment("N Night", 0.9).IsSameOverrideAs(Snapshot("Night", 0.6)).Should().BeTrue();
    }

    [Fact]
    public void IsSameOverrideAs_does_not_match_two_snapshots()
    {
        Snapshot("Night").IsSameOverrideAs(Snapshot("Night")).Should().BeFalse();
        Snapshot(null).IsSameOverrideAs(Snapshot(null)).Should().BeFalse();
    }

    [Fact]
    public void IsSameOverrideAs_does_not_match_two_treatments()
    {
        Treatment("N Night").IsSameOverrideAs(Treatment("N Night")).Should().BeFalse();
        Treatment(null).IsSameOverrideAs(Treatment(null)).Should().BeFalse();
    }

    [Theory]
    [InlineData("N Night", "Ride")]
    [InlineData("Avant repas", "repas")]
    public void IsSameOverrideAs_does_not_match_different_names(string reason, string name)
    {
        Treatment(reason).IsSameOverrideAs(Snapshot(name)).Should().BeFalse();
    }

    [Fact]
    public void IsSameOverrideAs_strips_a_symbol_of_several_code_points()
    {
        // One symbol from four code points joined by U+200D, as preset symbols often are.
        var symbol = string.Concat(new[] { 0x1F6B4, 0x200D, 0x2640, 0xFE0F }.Select(char.ConvertFromUtf32));

        Treatment(symbol + " Long Ride").IsSameOverrideAs(Snapshot("Long Ride")).Should().BeTrue();
        Treatment(symbol + " Long Ride").IsSameOverrideAs(Snapshot("Ride")).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.8, 0.8, true)]
    [InlineData(0.8, 0.6, false)]
    [InlineData(null, 1.0, true)]
    [InlineData(1.0, null, true)]
    public void IsSameOverrideAs_matches_an_unnamed_treatment_and_snapshot_by_scale_factor(
        double? factor, double? multiplier, bool expected)
    {
        Treatment(null, factor).IsSameOverrideAs(Snapshot(null, multiplier)).Should().Be(expected);
        Snapshot(null, multiplier).IsSameOverrideAs(Treatment(null, factor)).Should().Be(expected);
    }

    [Fact]
    public void IsSameOverrideAs_does_not_match_a_custom_treatment_to_an_unnamed_snapshot()
    {
        Treatment("Custom Override", 0.8).IsSameOverrideAs(Snapshot(null, 0.8)).Should().BeFalse();
    }
}
