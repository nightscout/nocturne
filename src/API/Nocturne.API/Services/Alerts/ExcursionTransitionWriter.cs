using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Persists a <see cref="TrackerDecision"/>, whichever engine made it: the excursion rows its
/// transition implies and the rule's tracker state, in one transaction
/// (<see cref="IAlertTrackerRepository.ExecuteInTransactionAsync{T}"/>). The host owns
/// excursion ids, so the decision's "has an excursion" becomes the prior excursion's id, or
/// the id of the one this write opens.
/// </summary>
internal static class ExcursionTransitionWriter
{
    /// <summary>
    /// Writes <paramref name="decision"/> against the rule's <paramref name="prior"/> state. A
    /// decision that changes no row opens no transaction (<see cref="Changes"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="autoResolved"/> means the same evaluation's auto-resolve pass closed
    /// whichever excursion the decision left active. The decision's post-state already reflects
    /// that close.
    /// <para>
    /// The write takes the rule's transition lock (<see cref="IAlertTrackerRepository.LockRuleAsync"/>)
    /// and re-reads the state under it. A state no longer equal to <paramref name="prior"/> was
    /// written by another process after this one read it, so the decision is stale: nothing is
    /// written and the transition is <see cref="ExcursionTransitionType.None"/>. That process has
    /// already made and acted on the transition this one would have.
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
        DateTime now,
        CancellationToken ct,
        bool autoResolved = false)
    {
        if (!Changes(prior, decision, autoResolved))
            return (Unwritten(prior, decision), null);

        var written = await repository.ExecuteInTransactionAsync(
            token => WriteAsync(repository, logger, ruleId, prior, decision, now, autoResolved, token),
            (attempt, token) => LandedAsync(repository, attempt, token),
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

    private static ExcursionTransition Unwritten(AlertTrackerState? prior, TrackerDecision decision) =>
        decision.Type == ExcursionTransitionType.ExcursionContinues
            ? new ExcursionTransition(decision.Type, prior?.ActiveExcursionId)
            : new ExcursionTransition(ExcursionTransitionType.None);

    private sealed record Written(
        ExcursionTransition Transition, ExcursionTransition? AutoResolve, AlertTrackerState? State);

    /// <summary>
    /// Whether an attempt whose commit reported failure committed anyway. Every attempt that
    /// writes anything writes a tracker state differing from the prior one, and the store holds
    /// either the prior state (rolled back) or the attempt's (committed).
    /// </summary>
    private static async Task<bool> LandedAsync(
        IAlertTrackerRepository repository, Written attempt, CancellationToken ct)
    {
        if (attempt.State is not { } wrote)
            return false;
        var stored = await repository.GetTrackerStateAsync(wrote.AlertRuleId, ct);
        return stored is not null
               && stored.State == wrote.State
               && stored.ConfirmationCount == wrote.ConfirmationCount
               && stored.ActiveExcursionId == wrote.ActiveExcursionId
               && stored.AwaitingRearm == wrote.AwaitingRearm
               && SameInstant(stored.UpdatedAt, wrote.UpdatedAt)
               && SameInstant(stored.HysteresisStartedAt, wrote.HysteresisStartedAt);
    }

    private static bool Same(AlertTrackerState? stored, AlertTrackerState? prior) =>
        stored is null || prior is null
            ? stored is null && prior is null
            : stored.State == prior.State
              && stored.ConfirmationCount == prior.ConfirmationCount
              && stored.ActiveExcursionId == prior.ActiveExcursionId
              && stored.AwaitingRearm == prior.AwaitingRearm
              && SameInstant(stored.UpdatedAt, prior.UpdatedAt)
              && SameInstant(stored.HysteresisStartedAt, prior.HysteresisStartedAt);

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
        DateTime now,
        bool autoResolved,
        CancellationToken ct)
    {
        await repository.LockRuleAsync(ruleId, ct);
        if (!Same(await repository.GetTrackerStateAsync(ruleId, ct), prior))
        {
            logger.LogInformation(
                "Tracker state of alert rule {AlertRuleId} changed since it was read; its transition was decided elsewhere",
                ruleId);
            return new Written(new ExcursionTransition(ExcursionTransitionType.None), null, null);
        }

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
        if (decision.Post is { } post
            && (StateChanged(prior, post) || (post.HasExcursion ? activeId : null) != priorId))
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
            };
            await repository.UpsertTrackerStateAsync(state, ct);
        }

        return new Written(transition, autoResolve, state);
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
