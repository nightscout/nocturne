using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

public class EngineNativeGateTests
{
    /// <summary>
    /// CI sets NOCTURNE_ALERTS_REQUIRE_NATIVE=1 after building the cdylib so a
    /// broken probe cannot silently skip this assembly's rust-engine tests. The gate
    /// itself is the shared <see cref="NativeLibraryGate"/> in the corpus
    /// generator's Harness.
    /// </summary>
    [Fact]
    public void Native_library_is_present_when_required() =>
        NativeLibraryGate.AssertPresentWhenRequired();
}

/// <summary>
/// Shared fixtures for the engine-seam tests: engine factories over the generator's
/// in-memory fakes and scenario-to-seam projections. Corpus location, wire-to-domain
/// conversions and the JSON deep-diff live in the generator's Harness
/// (<see cref="CorpusLocator"/>, <see cref="ScenarioConversions"/>,
/// <see cref="JsonNodeDiff"/>).
/// </summary>
internal static class EngineTestHarness
{
    public static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0002-000000000001");

    // -----------------------------------------------------------------------
    // Engine factories (over the generator's in-memory fakes)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="ManagedAlertEngine"/> with the evaluator registry sourced from
    /// the single AddAlertEvaluators registration (so the seam test can never drift behind
    /// the live engine) and the supplied in-memory state fakes. The returned provider owns
    /// the evaluator lifetimes; dispose it with the test.
    /// </summary>
    public static (ManagedAlertEngine Engine, ServiceProvider Provider) BuildManagedEngine(
        ManualTimeProvider time,
        IConditionTimerStore timerStore,
        InMemoryTrackerRepository trackerRepo)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(timerStore);
        services.AddAlertEvaluators();
        services.AddSingleton<ConditionEvaluatorRegistry>();
        var provider = services.BuildServiceProvider();

        var tracker = new ExcursionTracker(
            trackerRepo, new AlertRuleEvaluationGate(), time, NullLogger<ExcursionTracker>.Instance);
        var engine = new ManagedAlertEngine(
            provider.GetRequiredService<ConditionEvaluatorRegistry>(),
            tracker,
            NullLogger<ManagedAlertEngine>.Instance);
        return (engine, provider);
    }

    /// <summary>Builds a <see cref="RustBackedAlertEngine"/> over the same in-memory fakes.</summary>
    public static RustBackedAlertEngine BuildRustEngine(
        ManualTimeProvider time,
        IConditionTimerStore timerStore,
        InMemoryTrackerRepository trackerRepo)
    {
        var gate = new AlertRuleEvaluationGate();
        var tracker = new ExcursionTracker(trackerRepo, gate, time, NullLogger<ExcursionTracker>.Instance);
        return new RustBackedAlertEngine(
            timerStore,
            trackerRepo,
            tracker,
            gate,
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
            rule.AutoResolveParams?.GetRawText());
    }

    /// <summary>
    /// Assembles an <see cref="ExpectedRuleResult"/> from a seam evaluation plus the
    /// observable state in the fakes — the same projection the corpus generator records.
    /// </summary>
    public static async Task<ExpectedRuleResult> ToExpectedResultAsync(
        ScenarioRule rule,
        AlertEngineEvaluation evaluation,
        InMemoryTrackerRepository trackerRepo,
        RecordingTimerStore timerStore,
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
            Leaves = (evaluation.LeafValues ?? new Dictionary<int, bool>())
                .OrderBy(kv => kv.Key)
                .Select(kv => new ExpectedLeaf(kv.Key, kv.Value))
                .ToList(),
            Transition = RustEnvelopeMapper.TransitionToWire(evaluation.Transition.Type),
            CloseReason = RustEnvelopeMapper.CloseReasonToWire(evaluation.Transition.CloseReason),
            Tracker = state is null
                ? null
                : new ExpectedTrackerState
                {
                    State = state.State,
                    ConfirmationCount = state.ConfirmationCount,
                    Excursion = trackerRepo.OrdinalOf(state.ActiveExcursionId),
                },
            AutoResolved = evaluation.AutoResolved ? true : null,
            TimerOps = timerStore.DrainOps() is { Count: > 0 } ops ? ops : null,
        };
    }
}

/// <summary>Minimal capturing logger for asserting structured log events.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
