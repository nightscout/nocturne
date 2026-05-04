using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Manages the lifecycle of individual alert deliveries: dispatching notifications
/// through the rule's flat channel list and recording their outcome.
/// </summary>
/// <seealso cref="IAlertOrchestrator"/>
/// <seealso cref="AlertPayload"/>
public interface IAlertDeliveryService
{
    /// <summary>
    /// Dispatches the alert payload to every channel in <paramref name="channels"/> in parallel.
    /// One <c>alert_deliveries</c> row is written per channel before the provider call so the
    /// audit trail is complete even on provider failure.
    /// </summary>
    /// <param name="alertInstanceId">The alert instance the deliveries belong to.</param>
    /// <param name="channels">The rule's channel list (typically empty when the rule was created with no opt-in delivery surface).</param>
    /// <param name="payload">The <see cref="AlertPayload"/> rendered into each delivery.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DispatchAsync(
        Guid alertInstanceId,
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        CancellationToken ct);

    /// <summary>
    /// Test-fire dispatch path. Creates a synthetic <c>alert_instances</c> row marked
    /// <c>is_test=true</c> and sends through the rule's channels using the same provider
    /// chain as a real fire. The instance is excluded from active-alerts queries; deliveries
    /// are tagged so History can render the test-fire log distinctly. Used by the editor's
    /// Test Fire CTA and the per-row Test action.
    /// </summary>
    /// <param name="alertRuleId">The rule whose channels are being tested.</param>
    /// <param name="channels">Channels to fire through (typically the rule's saved channels; for dry-run flows the editor passes its in-memory list).</param>
    /// <param name="payload">A <see cref="AlertPayload"/> rendered by the caller — name is conventionally prefixed with <c>"[Test] "</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task TestFireAsync(
        Guid alertRuleId,
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        CancellationToken ct);

    /// <summary>
    /// Dry-run test fire for the editor on an unsaved rule. Broadcasts <c>alert_test_fire</c>
    /// and dispatches to providers, but writes no <c>alert_instances</c> or
    /// <c>alert_deliveries</c> rows — there is no rule to attribute them to. Use
    /// <see cref="TestFireAsync"/> for saved rules.
    /// </summary>
    Task TestFireDryRunAsync(
        IReadOnlyList<AlertRuleChannelSnapshot> channels,
        AlertPayload payload,
        CancellationToken ct);

    /// <summary>
    /// Records a successful delivery with optional platform-specific identifiers for
    /// future reference (e.g., editing or threading follow-up messages).
    /// </summary>
    Task MarkDeliveredAsync(Guid deliveryId, string? platformMessageId, string? platformThreadId, CancellationToken ct);

    /// <summary>
    /// Records a failed delivery attempt with the error details.
    /// </summary>
    Task MarkFailedAsync(Guid deliveryId, string error, CancellationToken ct);
}
