using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>
/// A hold as the host persists it (docs/alerts/engine-semantics.md §6.4): the first held evaluation
/// records its start and the facts it waits for, the evaluation that reaches the bound is written
/// though it changes only <c>UpdatedAt</c>, so a restored state reports it once, and an excursion
/// held open closes by the bound with its reason.
/// </summary>
public class HoldSeamTests
{
    private static readonly Guid RuleId = Guid.Parse("00000000-0000-0000-0000-0000000000a7");
    private static readonly DateTime T0 = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AlertRule Rule = new()
    {
        Id = RuleId,
        Name = "low",
        ConditionType = AlertConditionType.Threshold,
        ConditionParams = """{"schema_version":2,"operator":"<","value":70}""",
    };

    private static AlertRuleSnapshot Snapshot() => new(
        Rule.Id, EngineTestHarness.TenantId, Rule.Name, Rule.ConditionType, Rule.ConditionParams,
        AlertRuleSeverity.Warning, "{}", 0, false, null);

    private static readonly SensorContext NoReading = new()
    {
        LatestValue = null,
        LatestTimestamp = null,
        TrendRate = null,
        LastReadingAt = null,
    };

    private static SensorContext Low(DateTime at) => new()
    {
        LatestValue = 60,
        LatestTimestamp = at,
        TrendRate = null,
        LastReadingAt = at,
    };

    private sealed class Run(ManualTimeProvider time, InMemoryTrackerRepository store)
    {
        public InMemoryTrackerRepository Store => store;

        public Task<AlertEngineEvaluation> At(DateTime at, SensorContext context)
        {
            time.SetUtcNow(at);
            return EngineTestHarness.BuildRustEngine(time, store)
                .EvaluateRuleAsync(Snapshot(), context, AlertEngineOptions.Default, CancellationToken.None);
        }
    }

    private static Run NewRun() => new(new ManualTimeProvider(), new InMemoryTrackerRepository([Rule]));

    [NativeFact]
    public async Task A_hold_records_its_start_and_facts_and_reports_its_bound_once()
    {
        var run = NewRun();
        var bound = T0 + RustAlertEngine.HoldBound();

        var first = await run.At(T0, NoReading);
        first.UnknownFacts.Should().Equal(new HeldFact("glucose", "absent"));
        var held = await run.Store.GetTrackerStateAsync(RuleId);
        (held!.HeldSince, held.HeldFacts).Should().Be((T0, "glucose:absent"));

        (await run.At(bound.AddMinutes(-1), NoReading)).HeldTooLong.Should().BeFalse();
        (await run.At(bound, NoReading)).HeldTooLong.Should().BeTrue();
        (await run.Store.GetTrackerStateAsync(RuleId))!.UpdatedAt.Should().Be(bound,
            "the stored state is what keeps the bound from being reported again");
        (await run.At(bound.AddMinutes(5), NoReading)).HeldTooLong.Should().BeFalse();

        await run.At(bound.AddMinutes(10), Low(bound.AddMinutes(10)));
        (await run.Store.GetTrackerStateAsync(RuleId))!.HeldSince.Should().BeNull("a known root ends the hold");
    }

    [NativeFact]
    public async Task An_excursion_held_open_closes_by_the_bound_and_nothing_reopens_it()
    {
        var run = NewRun();
        var bound = T0.AddMinutes(5) + RustAlertEngine.HoldBound();

        (await run.At(T0, Low(T0))).Transition.Type.Should().Be(ExcursionTransitionType.ExcursionOpened);
        (await run.At(T0.AddMinutes(5), NoReading)).Transition.Type.Should().Be(ExcursionTransitionType.None);
        (await run.At(bound.AddMinutes(-1), NoReading)).Transition.Type.Should().Be(ExcursionTransitionType.None,
            "an alert held open never closes by the bound before it");

        var reached = await run.At(bound, NoReading);
        (reached.HeldTooLong, reached.Transition.Type).Should().Be((true, ExcursionTransitionType.HysteresisStarted));

        var closed = await run.At(bound.AddSeconds(30), NoReading);
        closed.Transition.Type.Should().Be(ExcursionTransitionType.ExcursionClosed);
        closed.Transition.CloseReason.Should().Be(ExcursionCloseReason.HoldBound);

        var after = await run.At(bound.AddMinutes(5), NoReading);
        (after.Transition.Type, after.HeldTooLong).Should().Be((ExcursionTransitionType.None, false));
    }
}
