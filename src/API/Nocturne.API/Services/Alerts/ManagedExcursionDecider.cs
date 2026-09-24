using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The in-process C# excursion state machine: idle → confirming → active → hysteresis → idle.
/// </summary>
internal sealed class ManagedExcursionDecider : IExcursionDecider
{
    public static readonly ManagedExcursionDecider Instance = new();

    private const string StateIdle = TrackerPostState.Idle;
    private const string StateConfirming = TrackerPostState.Confirming;
    private const string StateActive = TrackerPostState.Active;
    private const string StateHysteresis = TrackerPostState.Hysteresis;

    private static TrackerDecision None(TrackerPostState? post) => new(ExcursionTransitionType.None, null, post);

    /// <inheritdoc/>
    public TrackerDecision Process(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, bool conditionMet, DateTime now)
    {
        var s = TrackerPostState.Of(state) ?? new TrackerPostState(StateIdle, 0, false, now, null);

        var (type, reason, post) = s.State switch
        {
            StateIdle => Idle(s, config, conditionMet),
            StateConfirming => Confirming(s, config, conditionMet),
            StateActive => conditionMet
                ? (ExcursionTransitionType.ExcursionContinues, null, s)
                : (ExcursionTransitionType.HysteresisStarted, null,
                    s with { State = StateHysteresis, HysteresisStartedAt = now }),
            StateHysteresis => Hysteresis(s, config, conditionMet, now),
            _ => throw new System.Diagnostics.UnreachableException($"{nameof(TrackerPostState.Of)} reads every state as a known one"),
        };

        return new TrackerDecision(type, reason, post with { UpdatedAt = now });
    }

    private static (ExcursionTransitionType, ExcursionCloseReason?, TrackerPostState) Idle(
        TrackerPostState s, TrackerConfig config, bool conditionMet)
    {
        if (s.AwaitingRearm)
            return (ExcursionTransitionType.None, null, s with { AwaitingRearm = conditionMet });
        if (!conditionMet)
            return (ExcursionTransitionType.None, null, s);
        if (config.ConfirmationReadings <= 1)
            return Open(s);
        return (ExcursionTransitionType.None, null, s with { State = StateConfirming, ConfirmationCount = 1 });
    }

    private static (ExcursionTransitionType, ExcursionCloseReason?, TrackerPostState) Confirming(
        TrackerPostState s, TrackerConfig config, bool conditionMet)
    {
        if (!conditionMet)
            return (ExcursionTransitionType.None, null, s with { State = StateIdle, ConfirmationCount = 0 });

        var confirmed = s with { ConfirmationCount = s.ConfirmationCount + 1 };
        return confirmed.ConfirmationCount >= config.ConfirmationReadings
            ? Open(confirmed)
            : (ExcursionTransitionType.None, null, confirmed);
    }

    private static (ExcursionTransitionType, ExcursionCloseReason?, TrackerPostState) Open(TrackerPostState s) =>
        (ExcursionTransitionType.ExcursionOpened, null,
            s with { State = StateActive, ConfirmationCount = 0, HasExcursion = true });

    private static (ExcursionTransitionType, ExcursionCloseReason?, TrackerPostState) Hysteresis(
        TrackerPostState s, TrackerConfig config, bool conditionMet, DateTime now)
    {
        if (conditionMet)
            return (ExcursionTransitionType.HysteresisResumed, null,
                s with { State = StateActive, HysteresisStartedAt = null });

        var started = WithStart(s);
        return HysteresisElapsed(started, config, now)
            ? (ExcursionTransitionType.ExcursionClosed, ExcursionCloseReason.Hysteresis, Closed(started))
            : (ExcursionTransitionType.None, null, started);
    }

    /// <inheritdoc/>
    /// <remarks>An auto-resolve close of an active excursion leaves the rule awaiting re-arm.</remarks>
    public TrackerDecision ForceClose(
        Guid ruleId, AlertTrackerState? state, ExcursionCloseReason reason, DateTime now)
    {
        var s = TrackerPostState.Of(state);
        if (s is not { HasExcursion: true })
            return None(s);

        return new TrackerDecision(ExcursionTransitionType.ExcursionClosed, reason, Closed(s) with
        {
            UpdatedAt = now,
            AwaitingRearm = reason == ExcursionCloseReason.AutoResolve && s.State == StateActive,
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A hysteresis state without a start keeps the <c>UpdatedAt</c> it adopts, even when the
    /// window has not elapsed.
    /// </remarks>
    public TrackerDecision CloseElapsedHysteresis(
        Guid ruleId, AlertTrackerState? state, TrackerConfig config, DateTime now)
    {
        var s = TrackerPostState.Of(state);
        if (s is not { State: StateHysteresis })
            return None(s);

        var started = WithStart(s);
        return HysteresisElapsed(started, config, now)
            ? new TrackerDecision(
                ExcursionTransitionType.ExcursionClosed, ExcursionCloseReason.Hysteresis,
                Closed(started) with { UpdatedAt = now })
            : None(started);
    }

    /// <summary>
    /// A state persisted before <see cref="AlertTrackerState.HysteresisStartedAt"/> existed has
    /// none while in hysteresis. Its <see cref="AlertTrackerState.UpdatedAt"/> is adopted as the start.
    /// </summary>
    private static TrackerPostState WithStart(TrackerPostState s) =>
        s with { HysteresisStartedAt = s.HysteresisStartedAt ?? s.UpdatedAt };

    /// <summary><c>now - start &gt;= HysteresisMinutes</c>, so a non-positive window has always elapsed.</summary>
    private static bool HysteresisElapsed(TrackerPostState s, TrackerConfig config, DateTime now) =>
        now >= s.HysteresisStartedAt!.Value.AddMinutes(config.HysteresisMinutes);

    private static TrackerPostState Closed(TrackerPostState s) =>
        s with
        {
            State = StateIdle, ConfirmationCount = 0, HasExcursion = false, HysteresisStartedAt = null,
            AwaitingRearm = false,
        };
}
