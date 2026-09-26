using System.Text.Json;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// The Rust engine's evaluations outside the per-rule driver, as pure functions of explicit
/// state: one condition node (smart snooze, auto-resolve), and the sweep's auto-resolve
/// decision. <see cref="RustBackedAlertEngine"/> persists what they return, and the shadow
/// evaluator only compares it.
/// </summary>
internal static class RustAuxiliaryEvaluation
{
    /// <summary>One condition node through <c>evaluate_node</c>.</summary>
    /// <exception cref="RustAlertEngineException">The engine rejected the node or the request.</exception>
    public static RustEvaluateNodeResponse EvaluateNode(
        AlertEngineErrors errors,
        string engine,
        Guid ruleId,
        JsonElement node,
        string pathRoot,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers)
    {
        var request = new RustEvaluateNodeRequest
        {
            RuleId = ruleId,
            Node = node,
            Root = pathRoot,
            Context = RustEnvelopeMapper.BuildContext(context),
            Now = now,
            Timers = RustEnvelopeMapper.BuildTimers(timers),
        };
        return errors.Track("evaluate_node", engine, () => RustAlertEngine.EvaluateNode(request));
    }

    /// <summary>
    /// Whether auto-resolve is attempted at all: it is enabled with a tree, and the rule has an
    /// open excursion (<see cref="ExcursionTracker.ActiveExcursionOf"/>).
    /// </summary>
    public static bool AttemptsAutoResolve(AlertRuleSnapshot rule, AlertTrackerState? state) =>
        rule.AutoResolveEnabled
        && !string.IsNullOrWhiteSpace(rule.AutoResolveParams)
        && ExcursionTracker.ActiveExcursionOf(state) is not null;

    /// <summary>
    /// Evaluates the rule's auto-resolve tree under the <c>auto_resolve</c> path root and, when
    /// it holds, force-closes the excursion with reason auto. Call only when
    /// <see cref="AttemptsAutoResolve"/> is true. A tree that does not parse, or that the engine
    /// rejects, does not resolve (the managed engine skips it too).
    /// </summary>
    public static RustAutoResolveOutcome AutoResolve(
        AlertEngineErrors errors,
        string engine,
        AlertRuleSnapshot rule,
        SensorContext context,
        DateTime now,
        IReadOnlyDictionary<string, DateTime> timers,
        AlertTrackerState? state,
        ILogger logger)
    {
        RustEvaluateNodeResponse response;
        try
        {
            var node = RustEnvelopeMapper.ParseNode(rule.AutoResolveParams!);
            response = EvaluateNode(
                errors, engine, rule.Id, node, AlertConditionTypeNames.AutoResolvePathRoot, context, now, timers);
        }
        catch (Exception ex) when (ex is JsonException or RustAlertEngineException)
        {
            logger.LogWarning(ex, "Failed to parse AutoResolveParams for rule {AlertRuleId}; skipping", rule.Id);
            return new RustAutoResolveOutcome(null, null);
        }

        if (!response.Value!.Value)
            return new RustAutoResolveOutcome(response, null);

        var close = new RustExcursionDecider(errors, engine)
            .ForceClose(rule.Id, state, ExcursionCloseReason.AutoResolve, now);
        return new RustAutoResolveOutcome(response, close);
    }
}

/// <summary>What <see cref="RustAuxiliaryEvaluation.AutoResolve"/> decided.</summary>
/// <param name="Node">The tree's evaluation, or <see langword="null"/> when it could not be evaluated.</param>
/// <param name="Close">The force-close, or <see langword="null"/> when the tree did not hold.</param>
internal sealed record RustAutoResolveOutcome(RustEvaluateNodeResponse? Node, TrackerDecision? Close);
