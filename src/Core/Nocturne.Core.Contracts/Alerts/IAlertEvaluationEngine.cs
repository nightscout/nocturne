using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Per-call options for <see cref="IAlertEvaluationEngine.EvaluateRuleAsync"/>.
/// </summary>
public sealed record AlertEngineOptions
{
    /// <summary>
    /// When true, force-evaluate every leaf of the rule's condition tree in isolation
    /// (no short-circuit) and return the per-leaf truths keyed by pre-order leaf id.
    /// Replay/leaf-log semantics; live evaluation leaves this off.
    /// </summary>
    public bool IncludeLeafValues { get; init; }

    /// <summary>Default options: no leaf log.</summary>
    public static readonly AlertEngineOptions Default = new();
}

/// <summary>
/// Everything observable from one rule evaluation on one tick, as produced by an
/// <see cref="IAlertEvaluationEngine"/>: the root condition truth, the excursion
/// tracker transition, the (optional) auto-resolve force-close transition, and the
/// optional per-leaf force-eval log.
/// </summary>
public sealed record AlertEngineEvaluation
{
    /// <summary>
    /// True when no evaluator exists for the rule's root condition type
    /// (e.g. <c>signal_loss</c>): the rule was skipped entirely — no tracker call,
    /// no auto-resolve, and every other field is default.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>Root condition truth for this evaluation.</summary>
    public bool ConditionMet { get; init; }

    /// <summary>The excursion tracker transition produced by this evaluation.</summary>
    public ExcursionTransition Transition { get; init; } = new(ExcursionTransitionType.None);

    /// <summary>
    /// The transition produced by the unconditional post-evaluation auto-resolve pass
    /// (<see cref="ExcursionTransitionType.ExcursionClosed"/> with reason <c>auto</c> when the
    /// auto-resolve tree fired), or null when auto-resolve was not attempted / did not fire.
    /// </summary>
    public ExcursionTransition? AutoResolveTransition { get; init; }

    /// <summary>True when the auto-resolve pass force-closed the excursion this evaluation.</summary>
    public bool AutoResolved => AutoResolveTransition?.Type == ExcursionTransitionType.ExcursionClosed;

    /// <summary>
    /// Per-leaf force-eval truths keyed by pre-order leaf id. Populated only when
    /// <see cref="AlertEngineOptions.IncludeLeafValues"/> was requested.
    /// </summary>
    public IReadOnlyDictionary<int, bool>? LeafValues { get; init; }
}

/// <summary>
/// Engine seam for alert rule evaluation: one implementation wraps the in-process C#
/// evaluators ("managed"), another delegates to the Rust engine over FFI ("rust"), and a
/// third runs both and logs divergences ("shadow"). Selected at startup via the
/// <c>Alerts:Engine</c> configuration flag (default <c>managed</c>).
/// </summary>
/// <remarks>
/// Implementations own all evaluation-state persistence (sustained timers via
/// <see cref="IConditionTimerStore"/>, tracker state and excursion rows via the tracker
/// ports) so callers stay persistence-agnostic. Delivery, instance creation, DND
/// suppression and other side effects remain with the callers (orchestrator/sweep) —
/// the seam only reports what happened.
/// </remarks>
public interface IAlertEvaluationEngine
{
    /// <summary>
    /// Evaluates one rule for one tick, mirroring the orchestrator contract: root
    /// condition eval (rooted at the rule's canonical wire-string path) → excursion
    /// tracker advance → unconditional auto-resolve under the <c>auto_resolve</c> path
    /// root (skipped after a close, gated on an active excursion).
    /// </summary>
    /// <param name="rule">The rule snapshot to evaluate.</param>
    /// <param name="context">Enriched sensor context for the tick. <c>CurrentRuleId</c>/<c>CurrentPath</c> threading is owned by the engine.</param>
    /// <param name="options">Per-call options (leaf log).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AlertEngineEvaluation> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertEngineOptions options,
        CancellationToken ct);

    /// <summary>
    /// Evaluates a single condition tree outside the per-rule driver (no tracker, no
    /// auto-resolve) under a reserved path root — used for smart-snooze conditions
    /// (<c>snooze</c>) and other auxiliary evaluation scopes. Sustained timers inside the
    /// tree are keyed by <c>(ruleId, path)</c> exactly like rule-body timers.
    /// </summary>
    /// <param name="ruleId">The rule whose timer rows the tree's sustained nodes are keyed under.</param>
    /// <param name="node">The condition tree to evaluate.</param>
    /// <param name="context">Enriched sensor context. <c>CurrentRuleId</c>/<c>CurrentPath</c> threading is owned by the engine.</param>
    /// <param name="pathRoot">Root path segment (e.g. <c>snooze</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> EvaluateNodeAsync(
        Guid ruleId,
        ConditionNode node,
        SensorContext context,
        string pathRoot,
        CancellationToken ct);

    /// <summary>
    /// Evaluates the rule's auto-resolve tree (under the <c>auto_resolve</c> path root)
    /// and force-closes the active excursion when it fires — the sweep's periodic
    /// counterpart of the per-reading auto-resolve step inside
    /// <see cref="EvaluateRuleAsync"/>. Returns the close transition, or a
    /// <see cref="ExcursionTransitionType.None"/> transition when auto-resolve is not
    /// configured, no excursion is active, the tree is malformed, or it did not fire.
    /// </summary>
    /// <param name="rule">The rule whose auto-resolve params are evaluated.</param>
    /// <param name="context">Enriched sensor context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct);
}
