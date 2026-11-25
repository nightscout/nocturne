using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Alerts.Corpus;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.API.Tests.Services.BackgroundServices;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

public class EngineNativeGateTests
{
    /// <summary>
    /// CI sets NOCTURNE_ALERTS_REQUIRE_NATIVE=1 after building the cdylib so a
    /// broken probe cannot silently skip this assembly's rust-engine tests. The gate
    /// itself is the shared <see cref="NativeLibraryGate"/> in the corpus test support.
    /// </summary>
    [Fact]
    public void Native_library_is_present_when_required() =>
        NativeLibraryGate.AssertPresentWhenRequired();
}

/// <summary>
/// Shared fixtures for the engine-seam tests: engine factories over the corpus's in-memory fakes
/// and scenario-to-seam projections. Corpus location, wire-to-domain conversions and the JSON
/// deep-diff live in the corpus test support (<see cref="CorpusLocator"/>,
/// <see cref="ScenarioConversions"/>, <see cref="JsonNodeDiff"/>).
/// </summary>
internal static class EngineTestHarness
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0002-000000000001");

    // -----------------------------------------------------------------------
    // Engine factories (over the corpus's in-memory fakes)
    // -----------------------------------------------------------------------

    /// <summary>An <see cref="ExcursionTracker"/> deciding through the Rust engine, as production does.</summary>
    public static ExcursionTracker BuildRustTracker(
        TimeProvider time,
        IAlertTrackerRepository trackerRepo,
        AlertRuleEvaluationGate? gate = null) =>
        new(trackerRepo, gate ?? new AlertRuleEvaluationGate(), time, NullLogger<ExcursionTracker>.Instance,
            new RustExcursionDecider(new AlertEngineErrors(new TestMeterFactory(), time)));

    /// <summary>
    /// Builds a <see cref="RustBackedAlertEngine"/> over the corpus's in-memory fakes. Its
    /// <see cref="NotTrueRecordingFloor"/> starts at <paramref name="time"/>'s instant now, unless one is given.
    /// </summary>
    public static RustBackedAlertEngine BuildRustEngine(
        ManualTimeProvider time,
        IAlertTrackerRepository trackerRepo,
        NotTrueRecordingFloor? notTrueFloor = null)
    {
        return new RustBackedAlertEngine(
            trackerRepo,
            new AlertRuleEvaluationGate(),
            new AlertEngineErrors(new TestMeterFactory(), time),
            new ConditionVersionLog(),
            notTrueFloor ?? new NotTrueRecordingFloor(time),
            time,
            NullLogger<RustBackedAlertEngine>.Instance);
    }

    // -----------------------------------------------------------------------
    // Scenario-to-seam projections
    // -----------------------------------------------------------------------

    public static AlertRuleSnapshot ToSnapshot(ScenarioRule rule)
    {
        var conditionType = AlertConditionTypeNames.FromWireString(rule.ConditionType)
            ?? throw new InvalidOperationException(
                $"Scenario rule '{rule.Name}' has unknown condition_type '{rule.ConditionType}'");

        return new AlertRuleSnapshot(
            rule.Id,
            TenantId,
            rule.Name,
            conditionType,
            rule.ConditionParams.GetRawText(),
            AlertRuleSeverity.Warning,
            "{}",
            0,
            rule.AutoResolveEnabled,
            rule.AutoResolveParams?.GetRawText(),
            OnUnknown: rule.OnUnknown ?? AlertRuleOnUnknown.Hold,
            GlucoseFreshnessMinutes: rule.GlucoseFreshnessMinutes,
            EnabledAt: rule.EnabledAt);
    }

    /// <summary>
    /// Assembles an <see cref="ExpectedRuleResult"/> from a seam evaluation plus the
    /// observable state in the fakes, in the shape the corpus snapshots record.
    /// </summary>
    public static async Task<ExpectedRuleResult> ToExpectedResultAsync(
        ScenarioRule rule,
        AlertEngineEvaluation evaluation,
        InMemoryTrackerRepository trackerRepo,
        CancellationToken ct)
    {
        if (evaluation.Skipped)
        {
            return new ExpectedRuleResult { RuleId = rule.Id, Skipped = true };
        }

        var state = await trackerRepo.GetTrackerStateAsync(rule.Id, ct);

        return new ExpectedRuleResult
        {
            RuleId = rule.Id,
            Root = evaluation.ConditionMet,
            Unknown = evaluation.ConditionUnknown ? true : null,
            UnknownFacts = evaluation.ConditionUnknown
                ? evaluation.UnknownFacts.Select(f => new ExpectedUnknownFact(f.Fact, f.Reason)).ToList()
                : null,
            HeldTooLong = evaluation.HeldTooLong ? true : null,
            Leaves = (evaluation.LeafValues ?? new Dictionary<int, bool?>())
                .OrderBy(kv => kv.Key)
                .Select(kv => new ExpectedLeaf(kv.Key, kv.Value ?? false, kv.Value is null ? true : null))
                .ToList(),
            MetBy = evaluation.MetBy.Count > 0
                ? evaluation.MetBy
                    .Select(m => new ExpectedMetPoint(
                        m.LeafId,
                        AlertConditionTypeNames.ToWireString(m.Kind),
                        m.Curve is { } curve ? System.Text.Json.JsonSerializer.Serialize(curve).Trim('"') : null,
                        m.OffsetMinutes,
                        m.Value,
                        m.Limit,
                        m.LimitFact is { } fact ? System.Text.Json.JsonSerializer.Serialize(fact).Trim('"') : null,
                        m.LimitOffset))
                    .ToList()
                : null,
            Transition = CorpusWire.TransitionWire(evaluation.Transition.Type),
            CloseReason = evaluation.Transition.CloseReason is { } reason ? CorpusWire.CloseReasonWire(reason) : null,
            Tracker = state is null
                ? null
                : new ExpectedTrackerState
                {
                    State = state.State,
                    Excursion = trackerRepo.OrdinalOf(state.ActiveExcursionId),
                    HysteresisStartedAt = state.HysteresisStartedAt,
                    AwaitingRearm = state.AwaitingRearm ? true : null,
                    HeldSince = state.HeldSince,
                },
            AutoResolved = evaluation.AutoResolved ? true : null,
            TimerOps = trackerRepo.Timers.DrainOps() is { Count: > 0 } ops ? ops : null,
            NextChangeAt = evaluation.NextChangeAt,
            ClockAnomaly = evaluation.ClockAnomalies.Count > 0
                ? evaluation.ClockAnomalies
                    .Select(a => new ExpectedClockAnomaly(a.Fact, (long)a.Ahead.TotalMilliseconds))
                    .ToList()
                : null,
        };
    }
}
