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
    /// Writes <paramref name="decision"/> against the rule's <paramref name="prior"/> state. An
    /// unchanged state is not rewritten.
    /// </summary>
    /// <remarks>
    /// <paramref name="autoResolved"/> means the same evaluation's auto-resolve pass closed
    /// whichever excursion the decision left active. The decision's post-state already reflects
    /// that close.
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
        var written = await repository.ExecuteInTransactionAsync(
            token => WriteAsync(repository, logger, ruleId, prior, decision, now, autoResolved, token),
            (attempt, token) => LandedAsync(repository, attempt, token),
            ct);
        return (written.Transition, written.AutoResolve);
    }

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
               && SameInstant(stored.UpdatedAt, wrote.UpdatedAt)
               && SameInstant(stored.HysteresisStartedAt, wrote.HysteresisStartedAt);
    }

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
            && (post != TrackerPostState.Of(prior) || (post.HasExcursion ? activeId : null) != priorId))
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
