using System.Text.Json;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.Core.Contracts.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// Seam-level replay corpus: every replay scenario through the
/// <see cref="IAlertReplayEngine"/> must reproduce the committed snapshot.
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

    [NativeTheory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Replay_reproduces_the_committed_snapshot(string scenarioName)
    {
        var (scenario, expected) = await CorpusLocator.LoadReplayScenarioAsync(scenarioName);

        var actual = await ReplayScenarioRunner.RunAsync(scenario, CancellationToken.None);

        var failures = new List<string>();
        JsonNodeDiff.Compare(
            JsonSerializer.SerializeToNode(actual, CorpusJson.Options), expected,
            $"replay scenario {scenarioName}", "replay", failures);
        failures.Should().BeEmpty(string.Join("\n", failures));
    }
}
