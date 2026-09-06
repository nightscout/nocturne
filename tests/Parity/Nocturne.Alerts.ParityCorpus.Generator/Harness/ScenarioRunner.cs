using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Drives a scenario through the live C# alert engine and produces the expected
/// snapshot. The per-rule sequence deliberately mirrors
/// <c>AlertOrchestrator.EvaluateRuleAsync</c> (root eval with canonical wire-string root
/// path → excursion tracker → unconditional auto-resolve under the <c>auto_resolve</c>
/// path root), plus the replay path's force-eval of every leaf for the leaf log.
/// Anything that diverges from those two drivers is a bug in this runner, not a
/// behaviour to snapshot.
/// </summary>
public sealed class ScenarioRunner
{
    public async Task<ExpectedFile> RunAsync(ScenarioFile scenario, CancellationToken ct)
    {
        var rules = scenario.Rules.Select(ScenarioConversions.ToAlertRule).ToList();

        var time = new ManualTimeProvider();
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository(rules);
        var tracker = new ExcursionTracker(
            trackerRepo, new AlertRuleEvaluationGate(), time, NullLogger<ExcursionTracker>.Instance);

        // Mirrors AlertReplayService.BuildReplayServices: the evaluator set comes from the
        // single AddAlertEvaluators registration so the corpus can never drift behind the
        // live engine when evaluators are added.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IConditionTimerStore>(timerStore);
        services.AddAlertEvaluators();
        services.AddSingleton<ConditionEvaluatorRegistry>();
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ConditionEvaluatorRegistry>();
        var forceRunner = new ForceEvalRunner();

        var expectedTicks = new List<ExpectedTick>(scenario.Ticks.Count);

        foreach (var tick in scenario.Ticks)
        {
            var at = DateTime.SpecifyKind(tick.At, DateTimeKind.Utc);
            time.SetUtcNow(at);
            var context = ScenarioConversions.ToSensorContext(tick.Context);

            var ruleResults = new List<ExpectedRuleResult>(scenario.Rules.Count);

            foreach (var (scenarioRule, rule) in scenario.Rules.Zip(rules))
            {
                ruleResults.Add(await EvaluateRuleAsync(
                    scenarioRule, rule, context, registry, tracker, trackerRepo, timerStore, forceRunner, ct));
            }

            expectedTicks.Add(new ExpectedTick { At = at, Rules = ruleResults });
        }

        return new ExpectedFile { Scenario = scenario.Name, Ticks = expectedTicks };
    }

    private static async Task<ExpectedRuleResult> EvaluateRuleAsync(
        ScenarioRule scenarioRule,
        AlertRule rule,
        SensorContext context,
        ConditionEvaluatorRegistry registry,
        ExcursionTracker tracker,
        InMemoryTrackerRepository trackerRepo,
        RecordingTimerStore timerStore,
        ForceEvalRunner forceRunner,
        CancellationToken ct)
    {
        var evaluator = registry.GetEvaluator(rule.ConditionType);
        if (evaluator is null)
        {
            // Orchestrator parity: no evaluator (e.g. signal_loss as a root type) means the
            // rule is skipped entirely — no tracker call, no auto-resolve.
            return new ExpectedRuleResult { RuleId = rule.Id, Skipped = true };
        }

        var wire = AlertConditionTypeNames.ToWireString(rule.ConditionType);
        var rootContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = wire,
        };

        var conditionMet = await evaluator.EvaluateAsync(rule.ConditionParams, rootContext, ct);

        // Replay-parity leaf log: force-evaluate every leaf in isolation (no
        // short-circuit), using the rule-root context exactly as AlertReplayService does.
        // Leaves are stateless so this contributes no timer ops.
        var node = BuildFullNode(wire, scenarioRule.ConditionParams);
        var leafValues = await forceRunner.EvaluateAllLeavesAsync(node, rootContext, registry, ct);

        var transition = await tracker.ProcessEvaluationAsync(rule.Id, conditionMet, ct);

        var autoResolved = false;
        if (rule.AutoResolveEnabled && !string.IsNullOrWhiteSpace(rule.AutoResolveParams))
        {
            autoResolved = await TryAutoResolveAsync(rule, context, registry, tracker, ct);
        }

        var state = await trackerRepo.GetTrackerStateAsync(rule.Id, ct);

        return new ExpectedRuleResult
        {
            RuleId = rule.Id,
            Root = conditionMet,
            Leaves = leafValues
                .OrderBy(kv => kv.Key)
                .Select(kv => new ExpectedLeaf(kv.Key, kv.Value))
                .ToList(),
            Transition = TransitionWire(transition.Type),
            CloseReason = transition.CloseReason is { } reason ? CloseReasonWire(reason) : null,
            Tracker = state is null
                ? null
                : new ExpectedTrackerState
                {
                    State = state.State,
                    ConfirmationCount = state.ConfirmationCount,
                    Excursion = trackerRepo.OrdinalOf(state.ActiveExcursionId),
                },
            AutoResolved = autoResolved ? true : null,
            TimerOps = timerStore.DrainOps() is { Count: > 0 } ops ? ops : null,
        };
    }

    /// <summary>Mirrors <c>AlertOrchestrator.TryAutoResolveAsync</c>.</summary>
    private static async Task<bool> TryAutoResolveAsync(
        AlertRule rule,
        SensorContext context,
        ConditionEvaluatorRegistry registry,
        ExcursionTracker tracker,
        CancellationToken ct)
    {
        var activeExcursionId = await tracker.GetActiveExcursionIdAsync(rule.Id, ct);
        if (activeExcursionId is null)
            return false;

        ConditionNode? node;
        try
        {
            node = JsonSerializer.Deserialize<ConditionNode>(rule.AutoResolveParams!, EvaluatorJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (node is null)
            return false;

        var autoResolveContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = AlertConditionTypeNames.AutoResolvePathRoot,
        };

        var shouldResolve = await registry.EvaluateNodeAsync(node, autoResolveContext, ct);
        if (!shouldResolve)
            return false;

        var transition = await tracker.ForceCloseAsync(rule.Id, ExcursionCloseReason.AutoResolve, ct);
        return transition.Type == ExcursionTransitionType.ExcursionClosed;
    }

    /// <summary>
    /// Reassembles a full ConditionNode (<c>{"type": wire, "&lt;wire&gt;": payload}</c>) from
    /// the rule's stored payload, matching how the DB row's (condition_type,
    /// condition_params) pair is reconstituted by <c>AlertReplayService.BuildNodeForRule</c>.
    /// </summary>
    private static ConditionNode BuildFullNode(string wire, JsonElement payload)
    {
        var obj = new JsonObject
        {
            ["type"] = wire,
            [wire] = JsonNode.Parse(payload.GetRawText()),
        };
        return JsonSerializer.Deserialize<ConditionNode>(obj.ToJsonString(), EvaluatorJson.Options)
            ?? throw new InvalidOperationException($"Failed to parse condition node for type '{wire}'");
    }

    private static string TransitionWire(ExcursionTransitionType type) => type switch
    {
        ExcursionTransitionType.None => "none",
        ExcursionTransitionType.ExcursionOpened => "opened",
        ExcursionTransitionType.ExcursionContinues => "continues",
        ExcursionTransitionType.HysteresisStarted => "hysteresis_started",
        ExcursionTransitionType.HysteresisResumed => "hysteresis_resumed",
        ExcursionTransitionType.ExcursionClosed => "closed",
        _ => throw new InvalidOperationException($"Unknown transition type {type}"),
    };

    private static string CloseReasonWire(ExcursionCloseReason reason) => reason switch
    {
        ExcursionCloseReason.Hysteresis => "hysteresis",
        ExcursionCloseReason.AutoResolve => "auto",
        ExcursionCloseReason.Manual => "manual",
        _ => reason.ToString().ToLowerInvariant(),
    };
}
