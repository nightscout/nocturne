using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Moves a rule's excursion through idle → confirming → active → hysteresis → idle. Each call
/// loads the rule's state, asks an <see cref="IExcursionDecider"/> what happens, and persists
/// the answer through <see cref="ExcursionTransitionWriter"/>, all under the rule's
/// <see cref="AlertRuleEvaluationGate"/> lease.
/// </summary>
/// <remarks>
/// The decider is the engine: <see cref="ManagedExcursionDecider"/> by default, or the one
/// <c>Alerts:Engine</c> selects (see <c>AddAlertEvaluationEngine</c>).
/// </remarks>
/// <seealso cref="IExcursionTracker"/>
/// <seealso cref="IAlertTrackerRepository"/>
public class ExcursionTracker : IExcursionTracker
{
    private readonly IAlertTrackerRepository _repository;
    private readonly AlertRuleEvaluationGate _gate;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExcursionTracker> _logger;

    /// <summary>A tracker deciding with the managed state machine.</summary>
    public ExcursionTracker(
        IAlertTrackerRepository repository,
        AlertRuleEvaluationGate gate,
        TimeProvider timeProvider,
        ILogger<ExcursionTracker> logger)
        : this(repository, gate, timeProvider, logger, ManagedExcursionDecider.Instance)
    {
    }

    internal ExcursionTracker(
        IAlertTrackerRepository repository,
        AlertRuleEvaluationGate gate,
        TimeProvider timeProvider,
        ILogger<ExcursionTracker> logger,
        IExcursionDecider decider)
    {
        _repository = repository;
        _gate = gate;
        _timeProvider = timeProvider;
        _logger = logger;
        Decider = decider;
    }

    internal IExcursionDecider Decider { get; }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> ProcessEvaluationAsync(
        Guid alertRuleId,
        bool conditionMet,
        CancellationToken ct)
    {
        // A concurrent evaluation of the same rule waits on the lease and re-reads the committed
        // state. Unleased, both would open an excursion and dispatch off it.
        using var lease = await _gate.AcquireAsync(alertRuleId, ct);

        var rule = await _repository.GetRuleAsync(alertRuleId, ct);
        if (rule == null)
        {
            _logger.LogWarning("Alert rule {AlertRuleId} not found; skipping evaluation", alertRuleId);
            return new ExcursionTransition(ExcursionTransitionType.None);
        }

        var state = await _repository.GetTrackerStateAsync(alertRuleId, ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var decision = Decider.Process(alertRuleId, state, TrackerConfig.Of(rule), conditionMet, now);
        return await PersistAsync(alertRuleId, state, decision, now, ct);
    }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> CloseElapsedHysteresisAsync(Guid alertRuleId, CancellationToken ct)
    {
        using var lease = await _gate.AcquireAsync(alertRuleId, ct);

        var rule = await _repository.GetRuleAsync(alertRuleId, ct);
        if (rule is null)
            return new ExcursionTransition(ExcursionTransitionType.None);

        var state = await _repository.GetTrackerStateAsync(alertRuleId, ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var decision = Decider.CloseElapsedHysteresis(alertRuleId, state, TrackerConfig.Of(rule), now);
        return await PersistAsync(alertRuleId, state, decision, now, ct);
    }

    /// <inheritdoc/>
    public async Task<ExcursionTransition> ForceCloseAsync(
        Guid alertRuleId,
        ExcursionCloseReason reason,
        CancellationToken ct)
    {
        using var lease = await _gate.AcquireAsync(alertRuleId, ct);

        var state = await _repository.GetTrackerStateAsync(alertRuleId, ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var decision = Decider.ForceClose(alertRuleId, state, reason, now);
        return await PersistAsync(alertRuleId, state, decision, now, ct);
    }

    /// <inheritdoc/>
    public async Task<Guid?> GetActiveExcursionIdAsync(Guid alertRuleId, CancellationToken ct) =>
        ActiveExcursionOf(await _repository.GetTrackerStateAsync(alertRuleId, ct));

    /// <summary>The open excursion of a state that reads as active or in hysteresis.</summary>
    internal static Guid? ActiveExcursionOf(AlertTrackerState? state) =>
        TrackerPostState.Of(state)?.State is TrackerPostState.Active or TrackerPostState.Hysteresis
            ? state!.ActiveExcursionId
            : null;

    private async Task<ExcursionTransition> PersistAsync(
        Guid alertRuleId, AlertTrackerState? state, TrackerDecision decision, DateTime now, CancellationToken ct)
    {
        if (state is not null && !TrackerPostState.IsKnown(state.State))
        {
            _logger.LogWarning(
                "Alert rule {AlertRuleId} has an unknown tracker state {State}; read as {ReadAs}",
                alertRuleId, state.State, TrackerPostState.Of(state)!.State);
        }
        var (transition, _) = await ExcursionTransitionWriter.ApplyAsync(
            _repository, _logger, alertRuleId, state, decision, now, ct);
        return transition;
    }
}
