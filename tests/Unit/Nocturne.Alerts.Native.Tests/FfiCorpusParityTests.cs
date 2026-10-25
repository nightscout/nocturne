using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// Every golden-corpus scenario run through the Rust engine over the FFI envelope (threading
/// timer/tracker state between ticks exactly as the envelope contract defines) must match its
/// committed <c>.expected.json</c> snapshot field-for-field.
/// </summary>
public class FfiCorpusParityTests
{
    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in CorpusLocator.EnumerateScenarioNames())
            data.Add(name);
        return data;
    }

    [Fact]
    public void Corpus_contains_at_least_100_scenarios()
    {
        CorpusLocator.EnumerateScenarioNames().Should().HaveCountGreaterThanOrEqualTo(
            100, "the golden corpus is the cross-engine contract; a path bug must not silently shrink the suite");
    }

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Rust_ffi_reproduces_the_committed_snapshot(string scenarioName)
    {
        var (scenario, expected) = await CorpusLocator.LoadScenarioAsync(scenarioName);

        var ffi = RunScenarioThroughFfi(scenario);

        var failures = new List<string>();
        JsonNodeDiff.Compare(ffi, expected, $"scenario {scenarioName}", "ffi", failures);

        failures.Should().BeEmpty(
            "the Rust FFI must reproduce the committed snapshot; differences:\n{0}",
            string.Join("\n", failures));
    }

    /// <summary>
    /// Drives a scenario through <see cref="RustAlertEngine"/> per rule per
    /// tick, persisting the timers/tracker state envelopes between calls and
    /// threading the shared <c>next_excursion_ordinal</c> across rules in
    /// evaluation order, then reassembles the expected-file JSON shape.
    /// </summary>
    private static JsonNode RunScenarioThroughFfi(ScenarioFile scenario)
    {
        var timers = new Dictionary<Guid, Dictionary<string, DateTime>>();
        var trackers = new Dictionary<Guid, RustTrackerState>();
        var nextExcursionOrdinal = 1;

        var ticks = new JsonArray();
        foreach (var tick in scenario.Ticks)
        {
            var at = DateTime.SpecifyKind(tick.At, DateTimeKind.Utc);
            var context = JsonSerializer.SerializeToElement(tick.Context, CorpusJson.Options);

            var rules = new JsonArray();
            foreach (var rule in scenario.Rules)
            {
                var tracker = (trackers.GetValueOrDefault(rule.Id) ?? new RustTrackerState())
                    with
                { NextExcursionOrdinal = nextExcursionOrdinal };

                RustEvaluateResponse response;
                try
                {
                    response = RustAlertEngine.Evaluate(
                        ToRustRule(rule),
                        context,
                        at,
                        timers.GetValueOrDefault(rule.Id),
                        tracker);
                }
                catch (RustAlertEngineException ex) when (ex.Message.Contains("malformed condition_params for "))
                {
                    // The host skips a rule the engine cannot evaluate and keeps its state.
                    rules.Add(new JsonObject { ["rule_id"] = rule.Id.ToString(), ["skipped"] = true });
                    continue;
                }

                timers[rule.Id] = response.Timers ?? new Dictionary<string, DateTime>();
                if (response.Tracker is not null)
                {
                    trackers[rule.Id] = response.Tracker;
                    nextExcursionOrdinal = response.Tracker.NextExcursionOrdinal;
                }

                rules.Add(JsonNode.Parse(response.Result!.Value.GetRawText()));
            }

            ticks.Add(new JsonObject
            {
                ["at"] = at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                ["rules"] = rules,
            });
        }

        return new JsonObject
        {
            ["schema_version"] = 1,
            ["scenario"] = scenario.Name,
            ["ticks"] = ticks,
        };
    }

    private static RustAlertRule ToRustRule(ScenarioRule rule) => new()
    {
        Id = rule.Id,
        ConditionType = rule.ConditionType,
        ConditionParams = rule.ConditionParams,
        ConfirmationReadings = rule.ConfirmationReadings,
        HysteresisMinutes = rule.HysteresisMinutes,
        AutoResolveEnabled = rule.AutoResolveEnabled,
        AutoResolveParams = rule.AutoResolveParams,
    };
}
