using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Seam-level golden corpus: every corpus scenario is driven through the
/// <see cref="IAlertEvaluationEngine"/> seam (<c>RustBackedAlertEngine</c>) over in-memory
/// state fakes, and the observable outcome of every tick (root, leaves, transition,
/// close reason, tracker state, auto-resolve, timer ops) must match the committed
/// <c>.expected.json</c> snapshot, so the adapter's state threading and persistence mapping
/// reproduce the rows the core decides.
/// </summary>
public class AlertEngineCorpusTests
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
            100, "the golden corpus is the engine contract; a path bug must not silently shrink the suite");
    }

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Engine_reproduces_the_committed_snapshot(string scenarioName)
    {
        var (scenario, expected) = await CorpusLocator.LoadScenarioAsync(scenarioName);
        var actual = await RunScenarioAsync(scenario);
        AssertMatches(actual, expected, scenarioName);
    }

    /// <summary>
    /// Drives one scenario through the seam per rule per tick over the corpus's
    /// in-memory fakes (recording timer store + ordinal-guid tracker repository) and
    /// reassembles the expected-file JSON shape.
    /// </summary>
    private static async Task<JsonNode> RunScenarioAsync(ScenarioFile scenario)
    {
        var ct = CancellationToken.None;
        var rules = scenario.Rules.Select(ScenarioConversions.ToAlertRule).ToList();
        var snapshots = scenario.Rules.Select(EngineTestHarness.ToSnapshot).ToList();

        var time = new ManualTimeProvider();
        var trackerRepo = new InMemoryTrackerRepository(rules);

        var engine = EngineTestHarness.BuildRustEngine(time, trackerRepo);

        var options = new AlertEngineOptions { IncludeLeafValues = true };
        var expectedTicks = new List<ExpectedTick>(scenario.Ticks.Count);

        foreach (var tick in scenario.Ticks)
        {
            var at = DateTime.SpecifyKind(tick.At, DateTimeKind.Utc);
            time.SetUtcNow(at);
            var context = ScenarioConversions.ToSensorContext(tick.Context);

            var ruleResults = new List<ExpectedRuleResult>(scenario.Rules.Count);
            foreach (var (scenarioRule, snapshot) in scenario.Rules.Zip(snapshots))
            {
                AlertEngineEvaluation evaluation;
                try
                {
                    evaluation = await engine.EvaluateRuleAsync(snapshot, context, options, ct);
                }
                catch (RustAlertEngineException)
                {
                    // The orchestrator's per-rule catch skips a rule the engine cannot evaluate.
                    evaluation = new AlertEngineEvaluation { Skipped = true };
                }
                ruleResults.Add(await EngineTestHarness.ToExpectedResultAsync(
                    scenarioRule, evaluation, trackerRepo, ct));
            }

            expectedTicks.Add(new ExpectedTick { At = at, Rules = ruleResults });
        }

        var file = new ExpectedFile { Scenario = scenario.Name, Ticks = expectedTicks };
        return JsonSerializer.SerializeToNode(file, CorpusJson.Options)!;
    }

    private static void AssertMatches(JsonNode actual, JsonNode expected, string scenarioName)
    {
        var failures = new List<string>();
        JsonNodeDiff.Compare(actual, expected, $"scenario {scenarioName}", "engine", failures);
        failures.Should().BeEmpty(
            "the engine driven through the IAlertEvaluationEngine seam must reproduce the committed snapshot; differences:\n{0}",
            string.Join("\n", failures));
    }
}
