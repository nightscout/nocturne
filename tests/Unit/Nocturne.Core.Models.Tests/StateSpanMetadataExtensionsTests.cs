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
}
