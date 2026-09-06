using System.Text.Json;
using System.Text.Json.Nodes;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// The in-process C# implementation of <see cref="IAlertEvaluationEngine"/>: wraps
/// <see cref="ConditionEvaluatorRegistry"/> and <see cref="IExcursionTracker"/> into the
/// per-rule driver sequence the orchestrator historically composed inline
/// (root eval with the canonical wire-string root path → excursion tracker →
/// unconditional auto-resolve under the <c>auto_resolve</c> path root).
/// </summary>
/// <remarks>
/// The logic here is extracted verbatim from <c>AlertOrchestrator.EvaluateRuleAsync</c> /
/// <c>TryAutoResolveAsync</c>; the orchestrator keeps every side effect (instance
/// creation, delivery, DND suppression, info auto-ack) and consumes the transitions this
/// engine reports. Behaviour is pinned by the golden corpus
/// (<c>tests/Parity/AlertEngineCorpus</c>) and the seam-level corpus tests.
/// </remarks>
internal sealed class ManagedAlertEngine(
    ConditionEvaluatorRegistry evaluatorRegistry,
    IExcursionTracker excursionTracker,
    ILogger<ManagedAlertEngine> logger)
    : IAlertEvaluationEngine
{
    private readonly ForceEvalRunner _forceEvalRunner = new();

    /// <inheritdoc/>
    public async Task<AlertEngineEvaluation> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertEngineOptions options,
        CancellationToken ct)
    {
        var evaluator = evaluatorRegistry.GetEvaluator(rule.ConditionType);
        if (evaluator is null)
        {
            // Orchestrator parity: no evaluator (e.g. signal_loss as a root type) means
            // the rule is skipped entirely — no tracker call, no auto-resolve.
            logger.LogWarning("No evaluator registered for condition type '{ConditionType}'", rule.ConditionType);
            return new AlertEngineEvaluation { Skipped = true };
        }

        // Seed CurrentRuleId / CurrentPath so stateful evaluators (sustained) can key
        // persistent timers, and recursive evaluators (composite/not/sustained) can extend
        // the path as they descend. Root path is the rule's condition kind, e.g.
        // "composite" — matching the convention in ConditionPath.Walk.
        var rootContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = AlertConditionTypeNames.ToWireString(rule.ConditionType),
        };
        var conditionMet = await evaluator.EvaluateAsync(rule.ConditionParams, rootContext, ct);

        // Replay-parity leaf log (opt-in): force-evaluate every leaf in isolation (no
        // short-circuit) with the rule-root context, exactly as AlertReplayService does.
        // Leaves are stateless so this contributes no timer mutations.
        IReadOnlyDictionary<int, bool>? leafValues = null;
        if (options.IncludeLeafValues)
        {
            leafValues = await ForceEvaluateLeavesAsync(rule, rootContext, ct);
        }

        var transition = await excursionTracker.ProcessEvaluationAsync(rule.Id, conditionMet, ct);

        // Orchestrator parity: after a close (hysteresis expiry) the per-reading pass
        // returns without an auto-resolve attempt. (The attempt would be a no-op anyway —
        // the active-excursion gate fails once the tracker is idle — but skipping keeps
        // the call sequence byte-identical.)
        ExcursionTransition? autoResolveTransition = null;
        if (transition.Type != ExcursionTransitionType.ExcursionClosed)
        {
            autoResolveTransition = await TryAutoResolveAsync(rule, context, ct);
        }

        return new AlertEngineEvaluation
        {
            ConditionMet = conditionMet,
            Transition = transition,
            AutoResolveTransition = autoResolveTransition,
            LeafValues = leafValues,
        };
    }

    /// <inheritdoc/>
    public async Task<bool> EvaluateNodeAsync(
        Guid ruleId,
        ConditionNode node,
        SensorContext context,
        string pathRoot,
        CancellationToken ct)
    {
        var nodeContext = context with
        {
            CurrentRuleId = ruleId,
            CurrentPath = pathRoot,
        };
        return await evaluatorRegistry.EvaluateNodeAsync(node, nodeContext, ct);
    }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct)
    {
        return await TryAutoResolveAsync(rule, context, ct)
            ?? new ExcursionTransition(ExcursionTransitionType.None);
    }

    /// <summary>
    /// Extracted verbatim from <c>AlertOrchestrator.TryAutoResolveAsync</c>: evaluates
    /// <see cref="AlertRuleSnapshot.AutoResolveParams"/> against the enriched context under
    /// the <c>auto_resolve</c> path root and force-closes the active excursion when true.
    /// Returns null when auto-resolve was not attempted or did not fire; otherwise the
    /// <see cref="IExcursionTracker.ForceCloseAsync"/> transition.
    /// </summary>
    private async Task<ExcursionTransition?> TryAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct)
    {
        if (!rule.AutoResolveEnabled || string.IsNullOrWhiteSpace(rule.AutoResolveParams))
            return null;

        var activeExcursionId = await excursionTracker.GetActiveExcursionIdAsync(rule.Id, ct);
        if (activeExcursionId is null)
            return null;

        ConditionNode? node;
        try
        {
            node = JsonSerializer.Deserialize<ConditionNode>(rule.AutoResolveParams, EvaluatorJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}; skipping", rule.Id);
            return null;
        }

        if (node is null) return null;

        // Path-prefix auto-resolve so any nested sustained timers don't collide with
        // timers owned by the main rule body (which roots at e.g. "composite"). Both
        // per-reading (orchestrator) and periodic (sweep) auto-resolve paths share this
        // root — same (ruleId, path) timer row, by design.
        var autoResolveContext = context with
        {
            CurrentRuleId = rule.Id,
            CurrentPath = AlertConditionTypeNames.AutoResolvePathRoot,
        };

        bool shouldResolve;
        try
        {
            shouldResolve = await evaluatorRegistry.EvaluateNodeAsync(node, autoResolveContext, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-resolve evaluation failed for rule {AlertRuleId}", rule.Id);
            return null;
        }

        if (!shouldResolve) return null;

        return await excursionTracker.ForceCloseAsync(rule.Id, ExcursionCloseReason.AutoResolve, ct);
    }

    private async Task<IReadOnlyDictionary<int, bool>> ForceEvaluateLeavesAsync(
        AlertRuleSnapshot rule,
        SensorContext rootContext,
        CancellationToken ct)
    {
        ConditionNode node;
        try
        {
            node = BuildFullNode(AlertConditionTypeNames.ToWireString(rule.ConditionType), rule.ConditionParams);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex,
                "Failed to reconstruct condition node for rule {AlertRuleId}; skipping leaf log", rule.Id);
            return new Dictionary<int, bool>();
        }

        return await _forceEvalRunner.EvaluateAllLeavesAsync(node, rootContext, evaluatorRegistry, ct);
    }

    /// <summary>
    /// Reassembles a full ConditionNode (<c>{"type": wire, "&lt;wire&gt;": payload}</c>)
    /// from the rule's stored payload, matching how the DB row's
    /// <c>(condition_type, condition_params)</c> pair is reconstituted by
    /// <c>AlertReplayService.BuildNodeForRule</c> and the corpus harness.
    /// </summary>
    private static ConditionNode BuildFullNode(string wire, string payloadJson)
    {
        var obj = new JsonObject
        {
            ["type"] = wire,
            [wire] = string.IsNullOrWhiteSpace(payloadJson) ? null : JsonNode.Parse(payloadJson),
        };
        return JsonSerializer.Deserialize<ConditionNode>(obj.ToJsonString(), EvaluatorJson.Options)
            ?? throw new InvalidOperationException($"Failed to parse condition node for type '{wire}'");
    }
}
