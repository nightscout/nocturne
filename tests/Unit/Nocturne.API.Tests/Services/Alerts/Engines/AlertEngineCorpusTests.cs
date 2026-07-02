using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Core.Contracts.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Seam-level golden-corpus parity: every corpus scenario is driven through the
/// <see cref="IAlertEvaluationEngine"/> seam — <c>ManagedAlertEngine</c> always, and
/// <c>RustBackedAlertEngine</c> when the native library is available — over in-memory
/// state fakes, and the observable outcome of every tick (root, leaves, transition,
/// close reason, tracker state, auto-resolve, timer ops) must match the committed
/// <c>.expected.json</c> snapshot. This proves the seam adapters changed no semantics
/// on either side: the managed adapter extracts the orchestrator's driver verbatim, and
/// the rust adapter's state threading + persistence mapping reproduces the same rows.
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
            100, "the golden corpus is the cross-engine contract; a path bug must not silently shrink the suite");
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Managed_engine_reproduces_the_committed_snapshot(string scenarioName)
    {
        var (scenario, expected) = await CorpusLocator.LoadScenarioAsync(scenarioName);
        var actual = await RunScenarioAsync(scenario, useRustEngine: false);
        AssertMatches(actual, expected, scenarioName, "managed");
    }

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Rust_backed_engine_reproduces_the_committed_snapshot(string scenarioName)
    {
        var (scenario, expected) = await CorpusLocator.LoadScenarioAsync(scenarioName);
        var actual = await RunScenarioAsync(scenario, useRustEngine: true);
        AssertMatches(actual, expected, scenarioName, "rust");
    }

    /// <summary>
    /// Drives one scenario through the seam per rule per tick over the generator's
    /// in-memory fakes (recording timer store + ordinal-guid tracker repository) and
    /// reassembles the expected-file JSON shape.
    /// </summary>
    private static async Task<JsonNode> RunScenarioAsync(ScenarioFile scenario, bool useRustEngine)
    {
        var ct = CancellationToken.None;
        var rules = scenario.Rules.Select(ScenarioConversions.ToAlertRule).ToList();
        var snapshots = scenario.Rules.Select(EngineTestHarness.ToSnapshot).ToList();

        var time = new ManualTimeProvider();
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository(rules);

        ServiceProviderHolder? managedHolder = null;
        IAlertEvaluationEngine engine;
        if (useRustEngine)
        {
            engine = EngineTestHarness.BuildRustEngine(time, timerStore, trackerRepo);
        }
        else
        {
            var (managed, provider) = EngineTestHarness.BuildManagedEngine(time, timerStore, trackerRepo);
            engine = managed;
            managedHolder = new ServiceProviderHolder(provider);
        }

        await using var _ = managedHolder ?? new ServiceProviderHolder(null);

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
                var evaluation = await engine.EvaluateRuleAsync(snapshot, context, options, ct);
                ruleResults.Add(await EngineTestHarness.ToExpectedResultAsync(
                    scenarioRule, evaluation, trackerRepo, timerStore, ct));
            }

            expectedTicks.Add(new ExpectedTick { At = at, Rules = ruleResults });
        }

        var file = new ExpectedFile { Scenario = scenario.Name, Ticks = expectedTicks };
        return JsonSerializer.SerializeToNode(file, CorpusJson.Options)!;
    }

    private static void AssertMatches(JsonNode actual, JsonNode expected, string scenarioName, string engine)
    {
        var failures = new List<string>();
        JsonNodeDiff.Compare(actual, expected, $"scenario {scenarioName} ({engine})", engine, failures);
        failures.Should().BeEmpty(
            "the {0} engine driven through the IAlertEvaluationEngine seam must reproduce the committed snapshot; differences:\n{1}",
            engine, string.Join("\n", failures));
    }

    /// <summary>Disposes the managed engine's evaluator container at the end of the run.</summary>
    private sealed class ServiceProviderHolder(IAsyncDisposable? inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => inner?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}
