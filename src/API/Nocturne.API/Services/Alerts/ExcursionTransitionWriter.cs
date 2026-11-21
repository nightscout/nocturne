using System.Globalization;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// The next change (docs/alerts/engine-semantics.md §5.3) a full evaluation of a rule reported,
/// <see cref="At"/> null when it reported none, and the <see cref="Glucose"/> facts it was
/// computed from, for <see cref="ExcursionTransitionWriter"/> to record with its decision.
/// </summary>
internal readonly record struct NextChange(DateTime? At, string Glucose)
{
    /// <summary>
    /// The glucose facts of <paramref name="context"/>, the only facts a rule that waits for its
    /// next change reads. A value holds for a context only while these match: a reading deleted,
    /// edited or recomputed, or an evaluation that failed or was dropped, leaves a value computed
    /// from other facts. The glucose bucket is in it because evaluation derives the bucket from
    /// the schedule at its own time, so a context that has not derived it never matches one that
    /// has.
    /// </summary>
    public static string GlucoseOf(SensorContext context) => string.Join(
        '|',
        Instant(context.LatestTimestamp),
        Instant(context.LatestReceivedAt),
        context.LatestValue?.ToString(CultureInfo.InvariantCulture),
        context.TrendRate?.ToString(CultureInfo.InvariantCulture),
        context.TrendBucket?.ToString(),
        Instant(context.LastReadingAt),
        context.GlucoseBucket?.ToString());

    private static string? Instant(DateTime? at) => at?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// Persists a <see cref="TrackerDecision"/>: the excursion rows its transition implies, the rule's
/// tracker state and the sustained-timer deltas of the evaluation that made it, in one transaction
/// (<see cref="IAlertTrackerRepository.ExecuteInTransactionAsync{T}"/>). The host owns
/// excursion ids, so the decision's "has an excursion" becomes the prior excursion's id, or
/// the id of the one this write opens.
/// </summary>
internal static class ExcursionTransitionWriter
{
    /// <summary>
    /// Writes <paramref name="decision"/> against the rule's <paramref name="prior"/> state, and the
    /// <paramref name="timerOps"/> the same evaluation produced. A decision that changes no row and
    /// carries no timer op opens no transaction (<see cref="Changes"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="autoResolved"/> means the same evaluation's auto-resolve pass closed
    /// whichever excursion the decision left active. The decision's post-state already reflects
    /// that close.
    /// <para>
    /// The caller holds the rule's <see cref="AlertRuleEvaluationGate"/> lease. The write also takes
    /// the rule's transition lock, the backstop <see cref="IAlertTrackerRepository.LockRuleAsync"/>
    /// describes, and re-reads the state under it. A state no longer equal to
    /// <paramref name="prior"/> was written by an overlapping process after this one read it, so the
    /// decision is stale: nothing is written and the transition is
    /// <see cref="ExcursionTransitionType.None"/>. That process has already made and acted on the
    /// transition this one would have.
    /// </para>
    /// <para>
    /// A dropped decision drops its <paramref name="timerOps"/> too. They were made against the same
    /// tree, and a rule edit prunes timers to its new tree under this lock (<see cref="AlertRuleRearm"/>).
    /// </para>
    /// <para>
    /// A decision made against <paramref name="decidedAgainst"/> is stale too once the rule, read
    /// under the same lock, no longer holds those fields, and is dropped the same way. A rule edit
    /// saves the rule under this lock (<see cref="AlertRuleRearm"/>), so an evaluation that loaded
    /// the rule before the edit cannot land a hold, or a next change, the edit cleared. A decision
    /// that does not depend on the rule passes no <paramref name="decidedAgainst"/>.
    /// </para>
    /// <para>
    /// A full evaluation passes the <paramref name="nextChange"/> it reported, which is written
    /// whenever it differs from the stored one. Any other write leaves the rule's next change
    /// unknown, since it moved state the recorded value was read from.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The decision's transition with the host's excursion id, and the auto-resolve close when
    /// <paramref name="autoResolved"/> closed one.
    /// </returns>
    public static async Task<(ExcursionTransition Transition, ExcursionTransition? AutoResolve)> ApplyAsync(
        IAlertTrackerRepository repository,
        ILogger logger,
        Guid ruleId,
        AlertTrackerState? prior,
        TrackerDecision decision,
        AlertRuleConditions? decidedAgainst,
        DateTime now,
        CancellationToken ct,
        bool autoResolved = false,
        IReadOnlyList<RustTimerOp>? timerOps = null,
        NextChange? nextChange = null)
    {
        if (!Changes(prior, decision, autoResolved)
            && timerOps is not { Count: > 0 }
            && !(nextChange is not null && NextChangeMoved(prior, nextChange)))
            return (Unwritten(prior, decision), null);

        var written = await repository.ExecuteInTransactionAsync(
            (tx, token) => WriteAsync(
                tx, logger, ruleId, prior, decision, decidedAgainst, now, autoResolved, timerOps, nextChange, token),
            LandedAsync,
            ct);
        return (written.Transition, written.AutoResolve);
    }

    /// <summary>
    /// Whether <paramref name="decision"/> changes a stored row. A post-state differing from the
    /// prior one only in <see cref="TrackerPostState.UpdatedAt"/> does not. The only reader of
    /// <c>UpdatedAt</c> is a hysteresis state with no start, which adopts it as the start, and the
    /// decision that adopts it writes the start.
    /// </summary>
    private static bool Changes(AlertTrackerState? prior, TrackerDecision decision, bool autoResolved) =>
        decision.Type is not (ExcursionTransitionType.None or ExcursionTransitionType.ExcursionContinues)
        || autoResolved
        || StateChanged(prior, decision.Post);

    /// <summary>
    /// Compares with the stored fields as they are, so a stored state the decider read as another
    /// one is rewritten.
    /// </summary>
    private static bool StateChanged(AlertTrackerState? prior, TrackerPostState? post) =>
        post is not null
        && (prior is null
            || post.State != prior.State
            || post.ConfirmationCount != prior.ConfirmationCount
            || post.HasExcursion != prior.ActiveExcursionId.HasValue
            || post.HysteresisStartedAt != prior.HysteresisStartedAt
            || post.AwaitingRearm != prior.AwaitingRearm);

    /// <summary>
    /// Whether recording <paramref name="nextChange"/>, or none for a write that is not a full
    /// evaluation, changes what <paramref name="prior"/> holds.
    /// </summary>
    private static bool NextChangeMoved(AlertTrackerState? prior, NextChange? nextChange) =>
        nextChange is { } recorded
            ? prior is not { NextChangeKnown: true }
              || !SameInstant(prior.NextChangeAt, recorded.At)
              || prior.NextChangeGlucose != recorded.Glucose
            : prior is { NextChangeKnown: true };

    private static ExcursionTransition Unwritten(AlertTrackerState? prior, TrackerDecision decision) =>
        decision.Type == ExcursionTransitionType.ExcursionContinues
            ? new ExcursionTransition(decision.Type, prior?.ActiveExcursionId)
            : new ExcursionTransition(ExcursionTransitionType.None);

    private sealed record Written(
        ExcursionTransition Transition, ExcursionTransition? AutoResolve, AlertTrackerState? State, DateTime Now);

    /// <summary>
    /// Whether an attempt whose commit reported failure committed anyway, judged by the rows only
    /// that attempt could have written. The rule's lock is released at commit, so an overlapping
    /// process may have written the tracker state since: a committed open, close or hysteresis start is
    /// recognised by its excursion row instead, whose new id or <c>now</c> no other write shares.
    /// Only the transitions no excursion row records are judged by the tracker state.
    /// </summary>
    private static async Task<bool> LandedAsync(
        IAlertTrackerRepository repository, Written attempt, CancellationToken ct)
    {
        if (attempt.State is not { } wrote)
            return false;

        if (attempt.Transition is { Type: ExcursionTransitionType.ExcursionOpened, ExcursionId: { } opened })
            return await repository.GetExcursionAsync(opened, ct) is not null;

        var closed = attempt.AutoResolve?.ExcursionId
                     ?? (attempt.Transition.Type == ExcursionTransitionType.ExcursionClosed
                         ? attempt.Transition.ExcursionId
                         : null);
        if (closed is { } closedId)
            return await repository.GetExcursionAsync(closedId, ct) is { EndedAt: { } ended }
                   && SameInstant(ended, attempt.Now);

        if (attempt.Transition is { Type: ExcursionTransitionType.HysteresisStarted, ExcursionId: { } started })
            return await repository.GetExcursionAsync(started, ct) is { HysteresisStartedAt: { } at }
                   && SameInstant(at, attempt.Now);

        var stored = await repository.GetTrackerStateAsync(wrote.AlertRuleId, ct);
        return stored is not null
               && stored.State == wrote.State
               && stored.ConfirmationCount == wrote.ConfirmationCount
               && stored.ActiveExcursionId == wrote.ActiveExcursionId
               && stored.AwaitingRearm == wrote.AwaitingRearm
               && SameInstant(stored.UpdatedAt, wrote.UpdatedAt)
               && SameInstant(stored.HysteresisStartedAt, wrote.HysteresisStartedAt)
               && stored.NextChangeKnown == wrote.NextChangeKnown
               && SameInstant(stored.NextChangeAt, wrote.NextChangeAt)
               && stored.NextChangeGlucose == wrote.NextChangeGlucose;
    }

    private static bool Same(AlertTrackerState? stored, AlertTrackerState? prior) =>
        stored is null || prior is null
            ? stored is null && prior is null
            : stored.State == prior.State
              && stored.ConfirmationCount == prior.ConfirmationCount
              && stored.ActiveExcursionId == prior.ActiveExcursionId
              && stored.AwaitingRearm == prior.AwaitingRearm
              && SameInstant(stored.UpdatedAt, prior.UpdatedAt)
              && SameInstant(stored.HysteresisStartedAt, prior.HysteresisStartedAt)
              && stored.NextChangeKnown == prior.NextChangeKnown
              && SameInstant(stored.NextChangeAt, prior.NextChangeAt)
              && stored.NextChangeGlucose == prior.NextChangeGlucose;

    /// <summary>Equal to the microsecond, the precision the store keeps.</summary>
    private static bool SameInstant(DateTime? stored, DateTime? wrote) =>
        stored is { } a && wrote is { } b
            ? Math.Abs((a - b).Ticks) < TimeSpan.TicksPerMicrosecond
            : stored is null && wrote is null;

    private static async Task<Written> WriteAsync(
        IAlertTrackerRepository repository,
        ILogger logger,
        Guid ruleId,
        AlertTrackerState? prior,
        TrackerDecision decision,
        AlertRuleConditions? decidedAgainst,
        DateTime now,
        bool autoResolved,
        IReadOnlyList<RustTimerOp>? timerOps,
        NextChange? nextChange,
        CancellationToken ct)
    {
        await repository.LockRuleAsync(ruleId, ct);
        if (decidedAgainst is { } conditions && !conditions.HeldBy(await repository.GetRuleAsync(ruleId, ct)))
        {
            logger.LogInformation(
                "Alert rule {AlertRuleId} changed since its evaluation read it; its transition is dropped",
                ruleId);
            return new Written(new ExcursionTransition(ExcursionTransitionType.None), null, null, now);
        }
        if (!Same(await repository.GetTrackerStateAsync(ruleId, ct), prior))
        {
            logger.LogInformation(
                "Tracker state of alert rule {AlertRuleId} changed since it was read; its transition was decided elsewhere",
                ruleId);
            return new Written(new ExcursionTransition(ExcursionTransitionType.None), null, null, now);
        }

        await ApplyOpsAsync(repository.Timers, ruleId, timerOps, ct);

        var priorId = prior?.ActiveExcursionId;
        Guid? activeId = priorId;
        ExcursionTransition transition;

        switch (decision.Type)
        {
            case ExcursionTransitionType.ExcursionOpened:
                var opened = await repository.CreateExcursionAsync(ruleId, now, ct);
                logger.LogInformation(
                    "Excursion {ExcursionId} opened for alert rule {AlertRuleId}", opened.Id, ruleId);
                activeId = opened.Id;
                transition = new ExcursionTransition(decision.Type, opened.Id);
                break;

            case ExcursionTransitionType.HysteresisStarted:
                if (priorId is { } started)
                    await repository.SetHysteresisStartedAsync(started, now, ct);
                transition = new ExcursionTransition(decision.Type, priorId);
                break;

            case ExcursionTransitionType.HysteresisResumed:
                if (priorId is { } resumed)
                    await repository.ClearHysteresisAsync(resumed, ct);
                transition = new ExcursionTransition(decision.Type, priorId);
                break;

            case ExcursionTransitionType.ExcursionClosed:
                await CloseAsync(repository, logger, ruleId, priorId, decision.CloseReason, now, ct);
                activeId = null;
                transition = new ExcursionTransition(decision.Type, priorId, decision.CloseReason);
                break;

            case ExcursionTransitionType.ExcursionContinues:
                transition = new ExcursionTransition(decision.Type, priorId);
                break;

            default:
                transition = new ExcursionTransition(ExcursionTransitionType.None);
                break;
        }

        ExcursionTransition? autoResolve = null;
        if (autoResolved && activeId is { } resolved)
        {
            await CloseAsync(repository, logger, ruleId, resolved, ExcursionCloseReason.AutoResolve, now, ct);
            autoResolve = new ExcursionTransition(
                ExcursionTransitionType.ExcursionClosed, resolved, ExcursionCloseReason.AutoResolve);
            activeId = null;
        }

        AlertTrackerState? state = null;
        var nextChangeMoved = NextChangeMoved(prior, nextChange);
        if (decision.Post is { } post
            && (StateChanged(prior, post) || (post.HasExcursion ? activeId : null) != priorId || nextChangeMoved))
        {
            state = new AlertTrackerState
            {
                AlertRuleId = ruleId,
                State = post.State,
                ConfirmationCount = post.ConfirmationCount,
                ActiveExcursionId = post.HasExcursion ? activeId : null,
                UpdatedAt = post.UpdatedAt,
                HysteresisStartedAt = post.HysteresisStartedAt,
                AwaitingRearm = post.AwaitingRearm,
                NextChangeAt = nextChange?.At,
                NextChangeKnown = nextChange is not null,
                NextChangeGlucose = nextChange?.Glucose,
            };
        }
        else if (prior is not null && nextChange is null && nextChangeMoved)
        {
            state = new AlertTrackerState
            {
                AlertRuleId = ruleId,
                State = prior.State,
                ConfirmationCount = prior.ConfirmationCount,
                ActiveExcursionId = prior.ActiveExcursionId,
                UpdatedAt = prior.UpdatedAt,
                HysteresisStartedAt = prior.HysteresisStartedAt,
                AwaitingRearm = prior.AwaitingRearm,
            };
        }
        if (state is not null)
            await repository.UpsertTrackerStateAsync(state, ct);

        return new Written(transition, autoResolve, state, now);
    }

    /// <summary>
    /// Writes the <paramref name="timerOps"/> of an evaluation that makes no tracker decision, in a
    /// transaction under the rule's transition lock, when the rule read under that lock still holds
    /// what the evaluation read (<paramref name="evaluatedAgainst"/>). Otherwise it writes nothing,
    /// as a dropped decision drops its timers.
    /// </summary>
    /// <returns>Whether the rule still holds it, so the evaluation stands.</returns>
    public static Task<bool> ApplyTimerOpsAsync(
        IAlertTrackerRepository repository,
        ILogger logger,
        Guid ruleId,
        Func<AlertRule, bool> evaluatedAgainst,
        IReadOnlyList<RustTimerOp>? timerOps,
        CancellationToken ct) =>
        repository.ExecuteInTransactionAsync(async (tx, token) =>
        {
            await tx.LockRuleAsync(ruleId, token);
            if (await tx.GetRuleAsync(ruleId, token) is not { } rule || !evaluatedAgainst(rule))
            {
                logger.LogInformation(
                    "Alert rule {AlertRuleId} changed since its evaluation read it; its sustained timers are dropped",
                    ruleId);
                return false;
            }
            await ApplyOpsAsync(tx.Timers, ruleId, timerOps, token);
            return true;
        }, ct: ct);

    /// <summary>Applies timer deltas in the order the engine executed them.</summary>
    private static async Task ApplyOpsAsync(
        IConditionTimerStore timers, Guid ruleId, IReadOnlyList<RustTimerOp>? ops, CancellationToken ct)
    {
        foreach (var op in ops ?? [])
        {
            if (op.Op == RustTimerOpKind.Set)
                await timers.SetFirstTrueAsync(ruleId, op.Path, op.At!.Value, ct);
            else
                await timers.ClearAsync(ruleId, op.Path, ct);
        }
    }

    private static async Task CloseAsync(
        IAlertTrackerRepository repository,
        ILogger logger,
        Guid ruleId,
        Guid? excursionId,
        ExcursionCloseReason? reason,
        DateTime now,
        CancellationToken ct)
    {
        if (excursionId is not { } id)
            return;
        await repository.CloseExcursionAsync(id, now, ct);
        logger.LogInformation(
            "Excursion {ExcursionId} closed for alert rule {AlertRuleId}, reason={Reason}", id, ruleId, reason);
    }
}
