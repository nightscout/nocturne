using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// Three-way parity: every golden-corpus scenario is run through (a) the live
/// C# alert engine (via the corpus generator's ScenarioRunner harness) and
/// (b) the Rust engine over the FFI envelope (threading timer/tracker state
/// between ticks exactly as the envelope contract defines), and both must
/// match (c) the committed <c>.expected.json</c> snapshot field-for-field.
/// </summary>
public class ThreeWayParityTests
{
    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in EnumerateScenarioNames())
            data.Add(name);
        return data;
    }

    [Fact]
    public void Corpus_contains_at_least_100_scenarios()
    {
        EnumerateScenarioNames().Should().HaveCountGreaterThanOrEqualTo(
            100, "the golden corpus is the cross-engine contract; a path bug must not silently shrink the suite");
    }

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Csharp_engine_committed_snapshot_and_rust_ffi_agree(string scenarioName)
    {
        var corpusDir = CorpusDirectory();
        var scenarioJson = await File.ReadAllTextAsync(Path.Combine(corpusDir, $"{scenarioName}.json"));
        var expectedJson = await File.ReadAllTextAsync(Path.Combine(corpusDir, $"{scenarioName}.expected.json"));

        var scenario = JsonSerializer.Deserialize<ScenarioFile>(scenarioJson, CorpusJson.Options)
            ?? throw new InvalidOperationException($"Failed to parse scenario '{scenarioName}'");
        var expected = JsonNode.Parse(expectedJson)!;

        // (a) Live C# engine via the same harness that generated the corpus.
        var csharpResult = await new ScenarioRunner().RunAsync(scenario, CancellationToken.None);
        var csharp = JsonSerializer.SerializeToNode(csharpResult, CorpusJson.Options)!;

        // (b) Rust engine through the FFI envelope.
        var ffi = RunScenarioThroughFfi(scenario);

        var failures = new List<string>();
        JsonNodeDiff.Compare(csharp, expected, $"scenario {scenarioName}", "csharp", failures);
        JsonNodeDiff.Compare(ffi, expected, $"scenario {scenarioName}", "ffi", failures);

        failures.Should().BeEmpty(
            "the C# engine and the Rust FFI must both reproduce the committed snapshot; differences:\n{0}",
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

                var response = RustAlertEngine.Evaluate(
                    ToRustRule(rule),
                    context,
                    at,
                    timers.GetValueOrDefault(rule.Id),
                    tracker);

                timers[rule.Id] = response.Timers ?? new Dictionary<string, DateTime>();
                if (response.Tracker is not null)
                {
                    trackers[rule.Id] = response.Tracker;
                    nextExcursionOrdinal = response.Tracker.NextExcursionOrdinal;
                }

                var result = (JsonObject)JsonNode.Parse(response.Result!.Value.GetRawText())!;
                ApplySnoozeStep(rule, context, at, timers, result);
                rules.Add(result);
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

    /// <summary>
    /// Runs the rule's smart-snooze conditions through <c>nocturne_alerts_evaluate_node</c>
    /// with <c>root: "snooze"</c> and folds the outcome into the rule's result object —
    /// the same two-call composition the backend performs (orchestrator per reading, sweep
    /// per expiring snooze) and the only place the FFI's root-path override is exercised
    /// against the corpus.
    /// </summary>
    private static void ApplySnoozeStep(
        ScenarioRule rule,
        JsonElement context,
        DateTime at,
        Dictionary<Guid, Dictionary<string, DateTime>> timers,
        JsonObject result)
    {
        // A skipped rule (signal_loss) records nothing else, so there is nowhere to put a
        // snooze result. Production does not skip it; the generator rejects that combination
        // at authoring time, so no scenario reaches here with conditions configured.
        if (result["skipped"] is not null) return;
        if (rule.SnoozeConditions is not { Count: > 0 } conditions) return;

        var wrapped = new JsonObject
        {
            ["type"] = "composite",
            ["composite"] = new JsonObject
            {
                ["operator"] = "and",
                ["conditions"] = new JsonArray(
                    conditions.Select(c => (JsonNode?)JsonNode.Parse(c.GetRawText())).ToArray()),
            },
        };

        var response = RustAlertEngine.EvaluateNode(new RustEvaluateNodeRequest
        {
            RuleId = rule.Id,
            Node = JsonSerializer.Deserialize<JsonElement>(wrapped.ToJsonString()),
            Root = CorpusPathRoots.Snooze,
            Context = context,
            Now = at,
            Timers = timers.GetValueOrDefault(rule.Id),
        });

        timers[rule.Id] = response.Timers ?? new Dictionary<string, DateTime>();
        result["snooze_extend"] = response.Value;

        if (response.TimerOps is not { Count: > 0 } ops) return;

        // Ops from the rule-body call come first; the snapshot records both in execution
        // order under the rule's single timer_ops list.
        if (result["timer_ops"] is not JsonArray merged)
        {
            merged = new JsonArray();
            result["timer_ops"] = merged;
        }
        foreach (var op in ops)
        {
            var node = new JsonObject { ["op"] = op.Op, ["path"] = op.Path };
            if (op.At is { } opAt)
                node["at"] = opAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            merged.Add(node);
        }
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

    private static IReadOnlyList<string> EnumerateScenarioNames()
    {
        return Directory.EnumerateFiles(CorpusDirectory(), "*.json")
            .Where(p => !p.EndsWith(".expected.json", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    internal static string CorpusDirectory() =>
        Path.Combine(FindRepoRoot(), "tests", "Parity", "AlertEngineCorpus");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nocturne.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (nocturne.sln) above " + AppContext.BaseDirectory);
    }
}
