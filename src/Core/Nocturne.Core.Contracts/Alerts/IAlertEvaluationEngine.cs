using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Per-call options for <see cref="IAlertEvaluationEngine.EvaluateRuleAsync"/>.
/// </summary>
public sealed record AlertEngineOptions
{
    /// <summary>
    /// When true, force-evaluate every leaf of the rule's condition tree in isolation
    /// and return the per-leaf truths keyed by pre-order leaf id.
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
    /// True when no evaluator exists for the rule's root condition type: the rule was
    /// skipped entirely — no tracker call, no auto-resolve, and every other field is default.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>Root condition truth for this evaluation; false when <see cref="ConditionUnknown"/>.</summary>
    public bool ConditionMet { get; init; }

    /// <summary>
    /// The root was unknown: a fact it compares was missing (docs/alerts/engine-semantics.md
    /// §2.4). The rule's <c>on_unknown</c> decided what the tracker made of it.
    /// </summary>
    public bool ConditionUnknown { get; init; }

    /// <summary>
    /// When <see cref="ConditionUnknown"/>, the facts whose unknown reached the root, each with
    /// its reason (docs/alerts/engine-semantics.md §2.4); empty otherwise.
    /// </summary>
    public IReadOnlyList<HeldFact> UnknownFacts { get; init; } = [];

    /// <summary>The evaluation's hold reached the engine's bound, once per hold (§6.4).</summary>
    public bool HeldTooLong { get; init; }

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
    /// Per-leaf force-eval truths keyed by pre-order leaf id, null for a leaf that is unknown.
    /// Populated only when <see cref="AlertEngineOptions.IncludeLeafValues"/> was requested.
    /// </summary>
    public IReadOnlyDictionary<int, bool?>? LeafValues { get; init; }

    /// <summary>
    /// When an evaluation from the state this one left can next differ with only the clock
    /// moving (docs/alerts/engine-semantics.md §5.3), or null when none can.
    /// </summary>
    public DateTime? NextChangeAt { get; init; }

    /// <summary>
    /// The anchors the evaluation read more than 5 minutes ahead of the clock, which it read as
    /// unknown (docs/alerts/engine-semantics.md §5.4); empty when none was.
    /// </summary>
    public IReadOnlyList<ClockAnomaly> ClockAnomalies { get; init; } = [];

    /// <summary>
    /// What each true <c>predicted</c>, <c>eventual_glucose</c> and <c>carbs_required</c> condition of
    /// the body met its comparison with (docs/alerts/engine-semantics.md §3); empty when none did.
    /// </summary>
    public IReadOnlyList<AlertMetPoint> MetBy { get; init; } = [];
}

/// <summary>
/// An anchor an evaluation read more than 5 minutes ahead of the clock.
/// </summary>
/// <param name="Fact">The context field or evaluation state that held it, as the engine names it.</param>
/// <param name="Ahead">How far ahead of the clock it was.</param>
public sealed record ClockAnomaly(string Fact, TimeSpan Ahead);

/// <summary>
/// Engine seam for alert rule evaluation, implemented over the Rust engine
/// (<c>crates/nocturne-alerts-core</c>) through its FFI.
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
    /// <param name="context">Enriched sensor context for the tick.</param>
    /// <param name="options">Per-call options (leaf log).</param>
    Task<AlertEngineEvaluation> EvaluateRuleAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        AlertEngineOptions options,
        CancellationToken ct);

    /// <summary>
    /// Evaluates a rule's smart-snooze conditions, outside the per-rule driver (no tracker, no
    /// auto-resolve), under the reserved <c>snooze</c> path root. Sustained timers inside them are
    /// keyed by <c>(ruleId, path)</c> exactly like rule-body timers.
    /// </summary>
    /// <param name="ruleId">The rule the conditions were read from.</param>
    /// <param name="conditions">The rule's stored snooze <c>conditions</c> list, as read.</param>
    /// <param name="context">Enriched sensor context.</param>
    /// <returns>
    /// Whether the conditions hold. False, with no timer written, when the rule no longer stores
    /// <paramref name="conditions"/>: an extension decided on a replaced tree is not trusted, so the
    /// snooze lapses.
    /// </returns>
    Task<bool> EvaluateSnoozeAsync(
        Guid ruleId,
        JsonElement conditions,
        SensorContext context,
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
    Task<ExcursionTransition> EvaluateAutoResolveAsync(
        AlertRuleSnapshot rule,
        SensorContext context,
        CancellationToken ct);
}
