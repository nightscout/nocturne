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
    public const string Idle = "idle";
    public const string Confirming = "confirming";
    public const string Active = "active";
    public const string Hysteresis = "hysteresis";

    /// <summary>Whether <paramref name="state"/> names a state the state machine has.</summary>
    public static bool IsKnown(string? state) => state is Idle or Confirming or Active or Hysteresis;

    /// <summary>
    /// The stored state as the state machine reads it (docs/alerts/engine-semantics.md §6). A
    /// state string naming no state reads as <c>active</c> while the row holds an excursion, so
    /// that excursion goes on to close. Otherwise it reads as <c>idle</c>, so the rule can fire.
    /// Confirmation, hysteresis and re-arm start over.
    /// </summary>
    public static TrackerPostState? Of(AlertTrackerState? state)
    {
        if (state is null)
            return null;
        var hasExcursion = state.ActiveExcursionId is not null;
        return IsKnown(state.State)
            ? new TrackerPostState(
                state.State, state.ConfirmationCount, hasExcursion, state.UpdatedAt,
                state.HysteresisStartedAt, state.AwaitingRearm)
            : new TrackerPostState(hasExcursion ? Active : Idle, 0, hasExcursion, state.UpdatedAt, null);
    }
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
    /// <summary>
    /// One evaluation's truth. <paramref name="autoResolveMet"/> is the auto-resolve tree's, read
    /// only while the rule awaits re-arm (docs/alerts/engine-semantics.md §6.3).
    /// </summary>
    TrackerDecision Process(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, bool autoResolveMet,
        DateTime now);

    /// <summary>Closes the excursion, if there is one, from any state.</summary>
    TrackerDecision ForceClose(Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now);

    /// <summary>Closes an excursion in hysteresis once its window has elapsed.</summary>
    TrackerDecision CloseElapsedHysteresis(Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now);
}
