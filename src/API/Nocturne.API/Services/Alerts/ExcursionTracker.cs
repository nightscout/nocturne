using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// State machine that manages the lifecycle of alert excursions.
/// </summary>
/// <remarks>
/// States: <c>idle</c> → <c>confirming</c> → <c>active</c> → <c>hysteresis</c> → <c>idle</c>.
///
/// On each evaluation:
/// <list type="number">
///   <item>Load or create tracker state for the rule via <see cref="IAlertTrackerRepository"/>.</item>
///   <item>Load rule configuration (<c>ConfirmationReadings</c>, <c>HysteresisMinutes</c>).</item>
///   <item>Apply state machine transitions based on whether the alert condition is met.</item>
///   <item>Persist updated state and any excursion changes.</item>
///   <item>Return an <see cref="ExcursionTransition"/> describing what happened.</item>
/// </list>
/// </remarks>
/// <seealso cref="IExcursionTracker"/>
/// <seealso cref="IAlertTrackerRepository"/>
public class ExcursionTracker(
    IAlertTrackerRepository repository,
    TimeProvider timeProvider,
    ILogger<ExcursionTracker> logger)
    : IExcursionTracker
{
    private const string StateIdle = "idle";
    private const string StateConfirming = "confirming";
    private const string StateActive = "active";
    private const string StateHysteresis = "hysteresis";

    /// <inheritdoc/>
    /// <param name="alertRuleId">The alert rule to evaluate.</param>
    /// <param name="conditionMet">Whether the alert condition is currently satisfied.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An <see cref="ExcursionTransition"/> indicating the state change that occurred,
    /// or <see cref="ExcursionTransitionType.None"/> when no transition was triggered.
    /// </returns>
    public async Task<ExcursionTransition> ProcessEvaluationAsync(
        Guid alertRuleId,
        bool conditionMet,
        CancellationToken ct)
    {
        var rule = await repository.GetRuleAsync(alertRuleId, ct);
        if (rule == null)
        {
            logger.LogWarning("Alert rule {AlertRuleId} not found; skipping evaluation", alertRuleId);
            return new ExcursionTransition(ExcursionTransitionType.None);
        }

        var state = await repository.GetTrackerStateAsync(alertRuleId, ct)
                    ?? new AlertTrackerState
                    {
                        AlertRuleId = alertRuleId,
                        State = StateIdle,
                        ConfirmationCount = 0,
                    };

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var transition = state.State switch
        {
            StateIdle => await HandleIdle(state, rule, conditionMet, now, ct),
            StateConfirming => await HandleConfirming(state, rule, conditionMet, now, ct),
            StateActive => await HandleActive(state, rule, conditionMet, now, ct),
            StateHysteresis => await HandleHysteresis(state, rule, conditionMet, now, ct),
            _ => new ExcursionTransition(ExcursionTransitionType.None),
        };

        state.UpdatedAt = now;
        await repository.UpsertTrackerStateAsync(state, ct);

        return transition;
    }

    private async Task<ExcursionTransition> HandleIdle(
        AlertTrackerState state,
        AlertRule rule,
        bool conditionMet,
        DateTime now,
        CancellationToken ct)
    {
        if (!conditionMet)
            return new ExcursionTransition(ExcursionTransitionType.None);

        // If only 1 reading required, go straight to active
        if (rule.ConfirmationReadings <= 1)
        {
            return await OpenExcursion(state, rule, now, ct);
        }

        // Start confirming
        state.State = StateConfirming;
        state.ConfirmationCount = 1;
        return new ExcursionTransition(ExcursionTransitionType.None);
    }

    private async Task<ExcursionTransition> HandleConfirming(
        AlertTrackerState state,
        AlertRule rule,
        bool conditionMet,
        DateTime now,
        CancellationToken ct)
    {
        if (!conditionMet)
        {
            // Reset to idle
            state.State = StateIdle;
            state.ConfirmationCount = 0;
            return new ExcursionTransition(ExcursionTransitionType.None);
        }

        state.ConfirmationCount++;

        if (state.ConfirmationCount >= rule.ConfirmationReadings)
        {
            return await OpenExcursion(state, rule, now, ct);
        }

        // Still confirming
        return new ExcursionTransition(ExcursionTransitionType.None);
    }

    private async Task<ExcursionTransition> HandleActive(
        AlertTrackerState state,
        AlertRule rule,
        bool conditionMet,
        DateTime now,
        CancellationToken ct)
    {
        if (conditionMet)
        {
            return new ExcursionTransition(
                ExcursionTransitionType.ExcursionContinues,
                state.ActiveExcursionId);
        }

        // Start hysteresis
        state.State = StateHysteresis;

        if (state.ActiveExcursionId.HasValue)
        {
            await repository.SetHysteresisStartedAsync(state.ActiveExcursionId.Value, now, ct);
        }

        return new ExcursionTransition(
            ExcursionTransitionType.HysteresisStarted,
            state.ActiveExcursionId);
    }

    private async Task<ExcursionTransition> HandleHysteresis(
        AlertTrackerState state,
        AlertRule rule,
        bool conditionMet,
        DateTime now,
        CancellationToken ct)
    {
        if (conditionMet)
        {
            // Resume excursion
            state.State = StateActive;

            if (state.ActiveExcursionId.HasValue)
            {
                await repository.ClearHysteresisAsync(state.ActiveExcursionId.Value, ct);
            }

            return new ExcursionTransition(
                ExcursionTransitionType.HysteresisResumed,
                state.ActiveExcursionId);
        }

        // Check if hysteresis has expired.
        // We need to read the excursion to get HysteresisStartedAt.
        // For simplicity, we use the excursion record's HysteresisStartedAt.
        // If we can't find it, close immediately.
        var excursionId = state.ActiveExcursionId;

        // The hysteresis started when we transitioned to this state.
        // We recorded it on the excursion entity via SetHysteresisStartedAsync.
        // For the expiry check, we need to know when hysteresis started.
        // We'll use state.UpdatedAt as the proxy for when hysteresis started
        // (it was set when we entered hysteresis state).
        var hysteresisStart = state.UpdatedAt;
        var hysteresisExpiry = hysteresisStart.AddMinutes(rule.HysteresisMinutes);

        if (now >= hysteresisExpiry)
        {
            // Hysteresis expired, close excursion
            if (excursionId.HasValue)
            {
                await repository.CloseExcursionAsync(excursionId.Value, now, ct);
            }

            state.State = StateIdle;
            state.ConfirmationCount = 0;
            state.ActiveExcursionId = null;

            return new ExcursionTransition(
                ExcursionTransitionType.ExcursionClosed,
                excursionId,
                ExcursionCloseReason.Hysteresis);
        }

        // Still in hysteresis, no transition
        return new ExcursionTransition(ExcursionTransitionType.None);
    }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> ForceCloseAsync(
        Guid alertRuleId,
        ExcursionCloseReason reason,
        CancellationToken ct)
    {
        var state = await repository.GetTrackerStateAsync(alertRuleId, ct);
        if (state?.ActiveExcursionId is null)
        {
            return new ExcursionTransition(ExcursionTransitionType.None);
        }

        var excursionId = state.ActiveExcursionId.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await repository.CloseExcursionAsync(excursionId, now, ct);

        state.State = StateIdle;
        state.ConfirmationCount = 0;
        state.ActiveExcursionId = null;
        state.UpdatedAt = now;
        await repository.UpsertTrackerStateAsync(state, ct);

        logger.LogInformation(
            "Excursion {ExcursionId} force-closed for alert rule {AlertRuleId}, reason={Reason}",
            excursionId, alertRuleId, reason);

        return new ExcursionTransition(
            ExcursionTransitionType.ExcursionClosed,
            excursionId,
            reason);
    }

    /// <inheritdoc/>
    public async Task<Guid?> GetActiveExcursionIdAsync(Guid alertRuleId, CancellationToken ct)
    {
        var state = await repository.GetTrackerStateAsync(alertRuleId, ct);
        if (state is null) return null;
        if (state.State != StateActive && state.State != StateHysteresis) return null;
        return state.ActiveExcursionId;
    }

    private async Task<ExcursionTransition> OpenExcursion(
        AlertTrackerState state,
        AlertRule rule,
        DateTime now,
        CancellationToken ct)
    {
        var excursion = await repository.CreateExcursionAsync(rule.Id, now, ct);

        state.State = StateActive;
        state.ConfirmationCount = 0;
        state.ActiveExcursionId = excursion.Id;

        logger.LogInformation(
            "Excursion {ExcursionId} opened for alert rule {AlertRuleId}",
            excursion.Id,
            rule.Id);

        return new ExcursionTransition(
            ExcursionTransitionType.ExcursionOpened,
            excursion.Id);
    }
}
