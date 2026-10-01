using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Contracts.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Seam-level replay corpus parity: every replay scenario through each
/// <see cref="IAlertReplayEngine"/> must reproduce the committed snapshot, and shadow mode must
/// find nothing to report.
/// </summary>
public class ReplayCorpusTests
{
    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in CorpusLocator.EnumerateReplayScenarioNames())
            data.Add(name);
        return data;
    }

    [Fact]
    public void Replay_corpus_is_not_empty()
    {
        CorpusLocator.EnumerateReplayScenarioNames().Should().HaveCountGreaterThanOrEqualTo(9);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public Task Managed_replay_reproduces_the_committed_snapshot(string scenarioName) =>
        AssertReproducesAsync(ReplayScenarioRunner.Managed(), scenarioName, "managed");

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public Task Rust_replay_reproduces_the_committed_snapshot(string scenarioName) =>
        AssertReproducesAsync(ReplayScenarioRunner.Rust(), scenarioName, "rust");

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Shadow_replay_reports_no_divergence(string scenarioName)
    {
        var logger = new ListLogger<ShadowAlertReplayEngine>();
        var shadow = new ShadowAlertReplayEngine(ReplayScenarioRunner.Managed(), ReplayScenarioRunner.Rust(), logger);

        await AssertReproducesAsync(shadow, scenarioName, "shadow");
        await shadow.PendingComparison;

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    private static async Task AssertReproducesAsync(IAlertReplayEngine engine, string scenarioName, string label)
    {
        var (scenario, expected) = await CorpusLocator.LoadReplayScenarioAsync(scenarioName);

        var actual = await ReplayScenarioRunner.RunAsync(engine, scenario, CancellationToken.None);

        var failures = new List<string>();
        JsonNodeDiff.Compare(
            JsonSerializer.SerializeToNode(actual, CorpusJson.Options), expected,
            $"replay scenario {scenarioName}", label, failures);
        failures.Should().BeEmpty(string.Join("\n", failures));
    }
}
