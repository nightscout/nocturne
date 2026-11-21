using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Dispatches a new instance of a rule unless an active Do Not Disturb scope silences it
/// (ADR 0004). A silenced instance still keeps its row, so Replay can show "would have fired but
/// you were in DND", and records the suppressing scope on it (<c>dnd:lows</c>/<c>highs</c>/<c>all</c>).
/// Critical rules, and rules that allow it, pierce every scope (<see cref="DndSuppressionGate"/>).
/// Nothing re-sends a silenced instance once the scope lapses.
/// </summary>
internal static class DndGatedDispatch
{
    /// <returns>The scope that silenced the instance, or null when it was dispatched.</returns>
    public static Task<DndScope?> DispatchAsync(
        AlertRuleSnapshot rule,
        IReadOnlySet<DndScope> activeScopes,
        Guid instanceId,
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        IAlertRepository repository,
        IAlertDeliveryService deliveryService,
        ILogger logger,
        CancellationToken ct) =>
        DispatchAsync(
            rule.Name, rule.Severity, rule.AllowThroughDnd, rule.ScopeClass, activeScopes, instanceId, channels,
            payload, repository, deliveryService, logger, ct);

    /// <returns>The scope that silenced the instance, or null when it was dispatched.</returns>
    public static async Task<DndScope?> DispatchAsync(
        string ruleName,
        AlertRuleSeverity severity,
        bool allowThroughDnd,
        RuleScopeClass scopeClass,
        IReadOnlySet<DndScope> activeScopes,
        Guid instanceId,
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        IAlertRepository repository,
        IAlertDeliveryService deliveryService,
        ILogger logger,
        CancellationToken ct)
    {
        if (DndSuppressionGate.SuppressingScope(severity, allowThroughDnd, scopeClass, activeScopes) is not { } scope)
        {
            await deliveryService.DispatchAsync(instanceId, channels, payload, ct);
            return null;
        }

        await repository.MarkInstanceSuppressedAsync(instanceId, DndSuppressionGate.SuppressionReason(scope), ct);
        logger.LogInformation(
            "Alert instance {InstanceId} for rule {RuleName} suppressed by DND (scope {Scope})",
            instanceId, ruleName, scope);
        return scope;
    }
}
