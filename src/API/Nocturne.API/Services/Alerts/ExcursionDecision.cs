using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// A rule's tracker state after a decision, in engine-independent terms: the excursion is
/// only present or absent, since the host owns excursion ids.
/// </summary>
internal sealed record TrackerPostState(
    string State,
    int ConfirmationCount,
    bool HasExcursion,
    DateTime UpdatedAt,
    DateTime? HysteresisStartedAt,
    bool AwaitingRearm = false)
{
    public static TrackerPostState? Of(AlertTrackerState? state) =>
        state is null
            ? null
            : new TrackerPostState(
                state.State,
                state.ConfirmationCount,
                state.ActiveExcursionId is not null,
                state.UpdatedAt,
                state.HysteresisStartedAt,
                state.AwaitingRearm);
}

/// <summary>
/// What one tracker operation decided, before any of it is persisted. <c>Post</c> is the rule's
/// state afterwards, or <see langword="null"/> when it has none.
/// </summary>
internal sealed record TrackerDecision(
    ExcursionTransitionType Type,
    ExcursionCloseReason? CloseReason,
    TrackerPostState? Post);

/// <summary>The rule configuration the excursion state machine reads.</summary>
internal readonly record struct TrackerConfig(int ConfirmationReadings, int HysteresisMinutes)
{
    public static TrackerConfig Of(AlertRule rule) => new(rule.ConfirmationReadings, rule.HysteresisMinutes);
}

/// <summary>
/// The excursion state machine (docs/alerts/engine-semantics.md §6) as pure decisions over a
/// rule's persisted state. <see cref="ExcursionTracker"/> loads the state and persists the
/// decision through <see cref="ExcursionTransitionWriter"/>, whichever engine decided it.
/// </summary>
internal interface IExcursionDecider
{
    /// <summary>One evaluation's truth.</summary>
    TrackerDecision Process(Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, DateTime now);

    /// <summary>Closes the excursion, if there is one, from any state.</summary>
    TrackerDecision ForceClose(Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now);

    /// <summary>Closes an excursion in hysteresis once its window has elapsed.</summary>
    TrackerDecision CloseElapsedHysteresis(Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now);
}
