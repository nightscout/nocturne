using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Completeness guard for the FFI context wire contract, which exists as three
/// hand-synced shapes: <see cref="SensorContext"/> (the record evaluators read),
/// <c>RustEnvelopeMapper</c>'s private <c>Wire*</c> records (what the live engine sends
/// over the FFI), and the corpus <c>Scenario*</c> records (the cross-engine interchange
/// format). Every public property of the domain type must have a snake_case counterpart
/// on both wire shapes, or be listed in the pair's explicit exclusion set — so adding a
/// SensorContext field forces a conscious carry-or-exclude decision on both copies.
/// </summary>
public class WireContractCompletenessTests
{
    /// <param name="Domain">The evaluator-facing domain record.</param>
    /// <param name="WireTypeName">Name of the private nested record in <c>RustEnvelopeMapper</c>.</param>
    /// <param name="ScenarioType">The corpus interchange record.</param>
    /// <param name="Excluded">Domain properties that deliberately do not cross the FFI.</param>
    private sealed record ContractPair(Type Domain, string WireTypeName, Type ScenarioType, string[] Excluded);

    private static readonly ContractPair[] Pairs =
    [
        new(typeof(SensorContext), "WireContext", typeof(ScenarioContext),
        [
            // Per-call evaluation threading owned by the engine, not tick data.
            nameof(SensorContext.CurrentRuleId),
            nameof(SensorContext.CurrentPath),
            // Computed projection of ActiveTempBasal; exists only as a ReplayFact carrier.
            nameof(SensorContext.TempBasalRate),
            // Scoped DND suppression is applied host-side (DndSuppressionGate); the engine
            // only sees the tenant-wide ActiveDoNotDisturb snapshot.
            nameof(SensorContext.ActiveDndScopes),
        ]),
        new(typeof(PredictedGlucosePoint), "WirePrediction", typeof(ScenarioPrediction), []),
        // AlertId travels as the wire record's extra field (it is the dictionary key on
        // the domain side); the snapshot's own properties must all cross.
        new(typeof(ActiveAlertSnapshot), "WireActiveAlert", typeof(ScenarioActiveAlert), []),
        new(typeof(TempBasalSnapshot), "WireTempBasal", typeof(ScenarioTempBasal), []),
        new(typeof(OverrideSnapshot), "WireStartedSpan", typeof(ScenarioStartedSpan),
        [
            // The override evaluators read only StartedAt; the rest never crosses.
            nameof(OverrideSnapshot.EndsAt),
            nameof(OverrideSnapshot.Multiplier),
            nameof(OverrideSnapshot.Name),
        ]),
        new(typeof(PumpSuspensionSnapshot), "WireStartedSpan", typeof(ScenarioStartedSpan), []),
        new(typeof(DoNotDisturbSnapshot), "WireDoNotDisturb", typeof(ScenarioDoNotDisturb), []),
        new(typeof(PumpStateSnapshot), "WirePumpState", typeof(ScenarioPumpState), []),
        new(typeof(StateSpanSnapshot), "WireStateSpan", typeof(ScenarioStateSpan), []),
    ];

    [Fact]
    public void Every_sensor_context_property_crosses_the_wire_or_is_deliberately_excluded()
    {
        var failures = new List<string>();

        foreach (var pair in Pairs)
        {
            var wireType = typeof(RustEnvelopeMapper).GetNestedType(pair.WireTypeName, BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    $"RustEnvelopeMapper has no private nested type '{pair.WireTypeName}'");
            var wireNames = EffectiveWireNames(wireType);
            var scenarioNames = EffectiveWireNames(pair.ScenarioType);

            foreach (var property in pair.Domain.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (pair.Excluded.Contains(property.Name, StringComparer.Ordinal))
                    continue;

                var expected = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
                if (!wireNames.Contains(expected))
                    failures.Add(
                        $"{pair.Domain.Name}.{property.Name}: no '{expected}' on RustEnvelopeMapper.{pair.WireTypeName} " +
                        "(add it to the wire record or to the exclusion set)");
                if (!scenarioNames.Contains(expected))
                    failures.Add(
                        $"{pair.Domain.Name}.{property.Name}: no '{expected}' on {pair.ScenarioType.Name} " +
                        "(add it to the corpus record or to the exclusion set)");
            }
        }

        failures.Should().BeEmpty(
            "every domain property must cross the FFI on both hand-synced wire shapes or be consciously excluded:\n{0}",
            string.Join("\n", failures));
    }

    /// <summary>
    /// Effective JSON names of a wire record's properties: the explicit
    /// <see cref="JsonPropertyNameAttribute"/> when present, otherwise the snake_case
    /// policy name (the corpus serializer's <c>PropertyNamingPolicy</c>).
    /// </summary>
    private static HashSet<string> EffectiveWireNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name))
            .ToHashSet(StringComparer.Ordinal);
}
