using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
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
    private static readonly JsonSerializerOptions EnumWireOptions = BuildEnumWireOptions();

    private static JsonSerializerOptions BuildEnumWireOptions()
    {
        // TrendBucket carries JsonStringEnumMemberName attributes but no type-level
        // converter (the live engine never deserialises it from JSON — it arrives on the
        // SensorContext). The scenario wire format spells it in snake_case, so register
        // the converter here. StateSpanCategory/PumpModeState/GlucoseBucket have
        // type-level converters already.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<TrendBucket>());
        return options;
    }

    public async Task<ExpectedFile> RunAsync(ScenarioFile scenario, CancellationToken ct)
    {
        var rules = scenario.Rules.Select(ToAlertRule).ToList();

        var time = new ManualTimeProvider();
        var timerStore = new RecordingTimerStore();
        var trackerRepo = new InMemoryTrackerRepository(rules);
        var tracker = new ExcursionTracker(trackerRepo, time, NullLogger<ExcursionTracker>.Instance);

        // Mirrors AlertReplayService.BuildReplayServices: the evaluator set comes from the
        // single AddAlertEvaluators registration so the corpus can never drift behind the
        // live engine when evaluators are added.
        var services = new ServiceCollection();
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
            var context = ToSensorContext(tick.Context);

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
            //
            // The snapshot records nothing else for a skipped rule, so there is nowhere to
            // put a snooze result either. Production does NOT skip it: signal-loss rules open
            // excursions via AlertSweepService.EvaluateSignalLossAsync, and
            // CheckSnoozedInstancesAsync iterates instances with no filter on condition type.
            // Rather than silently pin nothing, reject the combination at authoring time.
            if (scenarioRule.SnoozeConditions is { Count: > 0 })
            {
                throw new InvalidOperationException(
                    $"Scenario rule '{scenarioRule.Name}' has snooze_conditions on condition_type "
                    + $"'{scenarioRule.ConditionType}', which the orchestrator skips. The snooze scope "
                    + "cannot be snapshotted for a skipped rule even though the sweep does evaluate it "
                    + "in production; cover it on an evaluable rule instead.");
            }

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

        var snoozeExtend = await TryEvaluateSnoozeAsync(scenarioRule, context, registry, ct);

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
            SnoozeExtend = snoozeExtend,
            TimerOps = timerStore.DrainOps() is { Count: > 0 } ops ? ops : null,
        };
    }

    /// <summary>
    /// Mirrors <c>AlertSweepService.EvaluateSnoozeConditionsAsync</c>. The sweep runs this
    /// on its own cadence (when a snooze expires), not per reading; folding it into the tick
    /// keeps the corpus one file per scenario while still pinning what the crate owns — the
    /// <c>snooze</c> path root and the timer keys a <c>sustained</c> inside it produces.
    /// </summary>
    private static async Task<bool?> TryEvaluateSnoozeAsync(
        ScenarioRule scenarioRule,
        SensorContext context,
        ConditionEvaluatorRegistry registry,
        CancellationToken ct)
    {
        if (scenarioRule.SnoozeConditions is not { Count: > 0 } rawConditions)
            return null;

        var conditions = rawConditions
            .Select(c => JsonSerializer.Deserialize<ConditionNode>(c.GetRawText(), EvaluatorJson.Options)
                ?? throw new InvalidOperationException(
                    $"Scenario rule '{scenarioRule.Name}' has a null snooze condition"))
            .ToList();

        var snoozeContext = context with
        {
            CurrentRuleId = scenarioRule.Id,
            CurrentPath = AlertConditionTypeNames.SnoozePathRoot,
        };

        return await registry.EvaluateNodeAsync(SnoozeConditionTree.Wrap(conditions), snoozeContext, ct);
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

    private static AlertRule ToAlertRule(ScenarioRule rule)
    {
        var conditionType = AlertConditionTypeNames.FromWireString(rule.ConditionType)
            ?? throw new InvalidOperationException(
                $"Scenario rule '{rule.Name}' has unknown condition_type '{rule.ConditionType}'");

        return new AlertRule
        {
            Id = rule.Id,
            Name = rule.Name,
            ConditionType = conditionType,
            ConditionParams = rule.ConditionParams.GetRawText(),
            ConfirmationReadings = rule.ConfirmationReadings,
            HysteresisMinutes = rule.HysteresisMinutes,
            AutoResolveEnabled = rule.AutoResolveEnabled,
            AutoResolveParams = rule.AutoResolveParams?.GetRawText(),
        };
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

    private static SensorContext ToSensorContext(ScenarioContext ctx)
    {
        return new SensorContext
        {
            LatestValue = ctx.LatestValue,
            LatestTimestamp = Utc(ctx.LatestTimestamp),
            TrendRate = ctx.TrendRate,
            LastReadingAt = Utc(ctx.LastReadingAt),
            TrendBucket = ParseWireEnum<TrendBucket>(ctx.TrendBucket),
            IobUnits = ctx.IobUnits,
            CobGrams = ctx.CobGrams,
            ReservoirUnits = ctx.ReservoirUnits,
            LastSiteChangeAt = Utc(ctx.LastSiteChangeAt),
            LastSensorStartAt = Utc(ctx.LastSensorStartAt),
            Predictions = ctx.Predictions?.Select(p => new PredictedGlucosePoint(p.OffsetMinutes, p.Mgdl)).ToList()
                ?? (IReadOnlyList<PredictedGlucosePoint>)Array.Empty<PredictedGlucosePoint>(),
            ActiveAlerts = ctx.ActiveAlerts?.ToDictionary(
                    a => a.AlertId,
                    a => new ActiveAlertSnapshot(a.State, Utc(a.TriggeredAt)!.Value, Utc(a.AcknowledgedAt)))
                ?? new Dictionary<Guid, ActiveAlertSnapshot>(),
            LastApsCycleAt = Utc(ctx.LastApsCycleAt),
            LastApsEnactedAt = Utc(ctx.LastApsEnactedAt),
            PumpBatteryPercent = ctx.PumpBatteryPercent,
            ActiveTempBasal = ctx.ActiveTempBasal is { } tb
                ? new TempBasalSnapshot(tb.Rate, tb.ScheduledRate, Utc(tb.StartedAt)!.Value)
                : null,
            UploaderBatteryPercent = ctx.UploaderBatteryPercent,
            ActiveOverride = ctx.ActiveOverride is { } ov
                ? new OverrideSnapshot(Utc(ov.StartedAt)!.Value, null, null, null)
                : null,
            ActivePumpSuspension = ctx.ActivePumpSuspension is { } ps
                ? new PumpSuspensionSnapshot(Utc(ps.StartedAt)!.Value)
                : null,
            SensitivityRatio = ctx.SensitivityRatio,
            ActiveDoNotDisturb = ctx.ActiveDoNotDisturb is { } dnd
                ? new DoNotDisturbSnapshot(Utc(dnd.StartedAt)!.Value, dnd.Source)
                : null,
            HasEverApsCycled = ctx.HasEverApsCycled,
            HasEverPumpSnapshot = ctx.HasEverPumpSnapshot,
            HasEverUploaderSnapshot = ctx.HasEverUploaderSnapshot,
            HasEverApsSensitivity = ctx.HasEverApsSensitivity,
            GlucoseBucket = ParseWireEnum<GlucoseBucket>(ctx.GlucoseBucket),
            LastCarbAt = Utc(ctx.LastCarbAt),
            LastBolusAt = Utc(ctx.LastBolusAt),
            TenantTimeZoneId = ctx.TenantTimeZoneId,
            ActivePumpState = ctx.ActivePumpState is { } pumpState
                ? new PumpStateSnapshot(
                    ParseWireEnum<PumpModeState>(pumpState.Mode)!.Value,
                    Utc(pumpState.StartedAt)!.Value)
                : null,
            ActiveStateSpans = ctx.ActiveStateSpans?.ToDictionary(
                    s => (ParseWireEnum<StateSpanCategory>(s.Category)!.Value, s.State),
                    s => new StateSpanSnapshot(
                        ParseWireEnum<StateSpanCategory>(s.Category)!.Value,
                        s.State,
                        Utc(s.StartedAt)!.Value))
                ?? new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>(),
        };
    }

    private static DateTime? Utc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static TEnum? ParseWireEnum<TEnum>(string? wire) where TEnum : struct, Enum =>
        wire is null ? null : JsonSerializer.Deserialize<TEnum>($"\"{wire}\"", EnumWireOptions);
}
