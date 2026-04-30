using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Advances an alert instance to its next escalation step, dispatching delivery
/// via <see cref="IAlertDeliveryService"/> and persisting the updated step order
/// through <see cref="IAlertRepository"/>.
/// </summary>
/// <remarks>
/// When no further escalation steps exist the instance status is set to
/// <c>triggered</c> and <c>NextEscalationAt</c> is cleared (set to
/// <see cref="DateTime.MinValue"/>), preventing further escalation attempts.
/// </remarks>
/// <seealso cref="IEscalationAdvancer"/>
/// <seealso cref="IAlertDeliveryService"/>
public class EscalationAdvancer : IEscalationAdvancer
{
    private readonly IAlertRepository _repository;
    private readonly IAlertDeliveryService _deliveryService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EscalationAdvancer> _logger;

    /// <summary>
    /// Initialises a new <see cref="EscalationAdvancer"/>.
    /// </summary>
    /// <param name="repository">Repository used to read escalation steps and persist instance state.</param>
    /// <param name="deliveryService">Service that dispatches the escalated alert payload.</param>
    /// <param name="timeProvider">Abstraction for the current UTC time, enabling deterministic testing.</param>
    /// <param name="logger">Logger instance.</param>
    public EscalationAdvancer(
        IAlertRepository repository,
        IAlertDeliveryService deliveryService,
        TimeProvider timeProvider,
        ILogger<EscalationAdvancer> logger)
    {
        _repository = repository;
        _deliveryService = deliveryService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <param name="instance">Snapshot of the alert instance to escalate.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AdvanceAsync(AlertInstanceSnapshot instance, CancellationToken ct)
    {
        var steps = await _repository.GetEscalationStepsAsync(instance.AlertScheduleId, ct);
        var nextStepOrder = instance.CurrentStepOrder + 1;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var nextStep = steps.FirstOrDefault(s => s.StepOrder == nextStepOrder);
        if (nextStep is null)
        {
            // No more steps; stop escalating
            await _repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(instance.Id,
                Status: "triggered", NextEscalationAt: DateTime.MinValue), ct);
            return;
        }

        var followingStep = steps.FirstOrDefault(s => s.StepOrder == nextStepOrder + 1);
        await _repository.UpdateInstanceAsync(new UpdateAlertInstanceRequest(instance.Id,
            CurrentStepOrder: nextStepOrder,
            Status: followingStep is null ? "triggered" : instance.Status,
            NextEscalationAt: followingStep is not null ? now.AddSeconds(nextStep.DelaySeconds) : DateTime.MinValue), ct);

        // Build payload and dispatch delivery
        var tenant = await _repository.GetTenantAlertContextAsync(instance.TenantId, ct);
        var activeCount = await _repository.CountActiveExcursionsAsync(instance.TenantId, ct);

        var payload = new AlertPayload
        {
            AlertType = AlertConditionType.Threshold,
            RuleName = "Escalated Alert",
            GlucoseValue = null,
            Trend = null,
            TrendRate = null,
            ReadingTimestamp = now,
            ExcursionId = instance.AlertExcursionId,
            InstanceId = instance.Id,
            TenantId = instance.TenantId,
            SubjectName = tenant?.SubjectName ?? "Unknown",
            ActiveExcursionCount = activeCount,
            // Severity is not on the instance — escalation only fires for non-Info rules
            // (Info pre-acks at trigger time and never escalates), so Warning is the safe
            // default until we plumb severity through the instance/excursion snapshot.
            Severity = AlertRuleSeverity.Warning,
        };

        await _deliveryService.DispatchAsync(instance.Id, nextStepOrder, payload, ct);
        _logger.LogInformation("Escalated instance {InstanceId} to step {StepOrder}", instance.Id, nextStepOrder);
    }
}
