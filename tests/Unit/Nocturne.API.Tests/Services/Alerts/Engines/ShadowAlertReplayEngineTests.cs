using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Tests.TestDoubles;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

public class ShadowAlertReplayEngineTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly AlertReplayInput Input = new([], []);

    private sealed class FixedEngine(Func<AlertReplayRun> run) : IAlertReplayEngine
    {
        public Task<AlertReplayRun> ReplayAsync(AlertReplayInput input, CancellationToken ct) => Task.FromResult(run());
    }

    private static AlertReplayRun Run(params AlertReplayRunEvent[] events) => new(
        [RuleId],
        events,
        [new AlertReplayRuleLeafLog(RuleId, [new LeafTransitionLog(0, [new LeafTransitionPoint(0, true)])])],
        null);

    private static (ShadowAlertReplayEngine Engine, ListLogger<ShadowAlertReplayEngine> Logger) Shadow(
        AlertReplayRun managed, Func<AlertReplayRun> rust)
    {
        var logger = new ListLogger<ShadowAlertReplayEngine>();
        return (new ShadowAlertReplayEngine(new FixedEngine(() => managed), new FixedEngine(rust), logger), logger);
    }

    [Fact]
    public async Task Agreeing_engines_log_nothing_and_answer_with_the_managed_run()
    {
        var managed = Run(new AlertReplayRunEvent(T0, RuleId, AlertReplayTransition.Fired));
        var (engine, logger) = Shadow(managed, () => Run(new AlertReplayRunEvent(T0, RuleId, AlertReplayTransition.Fired)));

        (await engine.ReplayAsync(Input, CancellationToken.None)).Should().BeSameAs(managed);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_differing_event_is_logged_as_a_divergence_for_its_rule()
    {
        var managed = Run(new AlertReplayRunEvent(T0, RuleId, AlertReplayTransition.Fired));
        var (engine, logger) = Shadow(managed, () => Run(new AlertReplayRunEvent(T0, RuleId, AlertReplayTransition.SuppressedByDnd)));

        (await engine.ReplayAsync(Input, CancellationToken.None)).Should().BeSameAs(managed);
        logger.Warnings.Should().ContainSingle().Which.Should()
            .Contain("AlertEngineDivergence").And.Contain(RuleId.ToString()).And.Contain("field=replay.events");
    }

    [Fact]
    public async Task A_failing_rust_replay_is_logged_and_the_managed_run_still_answers()
    {
        var managed = Run();
        var (engine, logger) = Shadow(managed, () => throw new RustAlertEngineException("boom"));

        (await engine.ReplayAsync(Input, CancellationToken.None)).Should().BeSameAs(managed);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception is RustAlertEngineException);
    }
}
