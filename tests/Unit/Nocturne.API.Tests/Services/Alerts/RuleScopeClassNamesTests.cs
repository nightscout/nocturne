using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Pins the host's <see cref="RuleScopeClassNames"/> switch to the
/// <see cref="RuleScopeClass"/> JSON wire contract (the <c>JsonStringEnumMemberName</c>
/// attributes) — the byte-identical device↔server strings for scoped Do Not Disturb
/// (ADR 0004). The native FFI parity matrix exercises the wire strings the engine
/// emits; this covers the enum↔wire mapping the host applies on top, without needing
/// the native library.
/// </summary>
public class RuleScopeClassNamesTests
{
    [Theory]
    [InlineData(RuleScopeClass.Low, "low")]
    [InlineData(RuleScopeClass.High, "high")]
    [InlineData(RuleScopeClass.Composite, "composite")]
    [InlineData(RuleScopeClass.Undirected, "undirected")]
    public void ToWireString_matches_the_json_wire_contract(RuleScopeClass scopeClass, string expected)
    {
        RuleScopeClassNames.ToWireString(scopeClass).Should().Be(expected);

        // The wire string must equal what System.Text.Json emits for the same value,
        // since that is the contract the API response and the device both deserialise.
        var json = JsonSerializer.Serialize(scopeClass).Trim('"');
        json.Should().Be(expected);
    }

    [Theory]
    [InlineData("low", RuleScopeClass.Low)]
    [InlineData("high", RuleScopeClass.High)]
    [InlineData("composite", RuleScopeClass.Composite)]
    [InlineData("undirected", RuleScopeClass.Undirected)]
    public void FromWireString_round_trips_every_value(string wire, RuleScopeClass expected)
    {
        RuleScopeClassNames.FromWireString(wire).Should().Be(expected);
    }

    [Fact]
    public void FromWireString_returns_null_for_an_unknown_value()
    {
        RuleScopeClassNames.FromWireString("sideways").Should().BeNull();
    }
}
