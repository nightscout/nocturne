using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Describes the possible state transitions for a glucose excursion
/// tracked by <see cref="IExcursionTracker"/>.
/// </summary>
public enum ExcursionTransitionType
{
    /// <summary>No state change occurred.</summary>
    None,

    /// <summary>A new excursion has been opened (condition first met).</summary>
    ExcursionOpened,

    /// <summary>An existing excursion continues (condition still met).</summary>
    ExcursionContinues,

    /// <summary>The condition cleared but the excursion entered hysteresis cool-down.</summary>
    HysteresisStarted,

    /// <summary>The condition re-triggered during hysteresis, resuming the excursion.</summary>
    HysteresisResumed,

    /// <summary>The excursion has been closed (condition cleared and hysteresis expired).</summary>
    ExcursionClosed
}

/// <summary>
/// Represents the result of an excursion evaluation, pairing the
/// <see cref="ExcursionTransitionType"/> with the excursion identifier when applicable.
/// </summary>
/// <param name="Type">The transition that occurred.</param>
/// <param name="ExcursionId">The <see cref="Nocturne.Core.Models.AlertExcursion"/> identifier, if an excursion is active.</param>
/// <param name="CloseReason">When <paramref name="Type"/> is <see cref="ExcursionTransitionType.ExcursionClosed"/>, why the excursion closed. Null for non-close transitions.</param>
public record ExcursionTransition(
    ExcursionTransitionType Type,
    Guid? ExcursionId = null,
    ExcursionCloseReason? CloseReason = null);

/// <summary>
/// Tracks the state-machine lifecycle of glucose excursions for a single
/// <see cref="Nocturne.Core.Models.AlertRule"/>. Each evaluation advances the
/// excursion through its states: opened, continuing, hysteresis, or closed.
/// </summary>
/// <remarks>
/// Excursion state is persisted via <see cref="Nocturne.Core.Contracts.Repositories.IAlertTrackerRepository"/>
/// so that excursions survive process restarts.
/// </remarks>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="IConditionEvaluator"/>
public interface IExcursionTracker
{
    /// <summary>
    /// Processes a single condition evaluation for an alert rule and returns the
    /// resulting excursion state transition.
    /// </summary>
    /// <param name="alertRuleId">The <see cref="Nocturne.Core.Models.AlertRule"/> being evaluated.</param>
    /// <param name="conditionMet">Whether the alert condition is currently met.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="ExcursionTransition"/> describing the state change.</returns>
    Task<ExcursionTransition> ProcessEvaluationAsync(Guid alertRuleId, bool conditionMet, CancellationToken ct);

    /// <summary>
    /// Closes any active excursion for the rule out-of-band from the per-reading
    /// state machine. Used by auto-resolve, manual close actions, and
    /// rule-disable cleanup. Resets tracker state to <c>idle</c>.
    /// </summary>
    /// <param name="alertRuleId">The rule whose active excursion should be closed.</param>
    /// <param name="reason">Why the excursion is being closed. Stamped on resolved instances.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="ExcursionTransition"/> of type <see cref="ExcursionTransitionType.ExcursionClosed"/>
    /// with the closed excursion id and <paramref name="reason"/>, or
    /// <see cref="ExcursionTransitionType.None"/> if the rule had no active excursion.
    /// </returns>
    Task<ExcursionTransition> ForceCloseAsync(Guid alertRuleId, ExcursionCloseReason reason, CancellationToken ct);

    /// <summary>
    /// Returns the open excursion id for the rule, or <see langword="null"/>
    /// when the rule is idle/confirming. Both <c>active</c> and <c>hysteresis</c>
    /// states count as having an open excursion (the underlying record has
    /// <c>EndedAt IS NULL</c>).
    /// </summary>
    /// <param name="alertRuleId">The rule to query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Guid?> GetActiveExcursionIdAsync(Guid alertRuleId, CancellationToken ct);
}
