namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Service for acknowledging active alert instances, silencing further escalation
/// until a new excursion begins.
/// </summary>
/// <seealso cref="IAlertOrchestrator"/>
public interface IAlertAcknowledgementService
{
    /// <summary>
    /// Acknowledges all active alert instances for the specified tenant, halting
    /// escalation delivery for those instances.
    /// </summary>
    /// <param name="tenantId">The tenant whose alerts should be acknowledged.</param>
    /// <param name="acknowledgedBy">Identifier of the user or system performing the acknowledgement.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when all active instances have been acknowledged.</returns>
    Task AcknowledgeAllAsync(Guid tenantId, string acknowledgedBy, CancellationToken ct);

    /// <summary>
    /// Acknowledges every unresolved <see cref="Nocturne.Infrastructure.Data.Entities.AlertInstanceEntity"/>
    /// belonging to the specified excursion. No-op when the excursion is already
    /// closed or already acknowledged. Used by per-rule auto-acknowledgement
    /// (Info-severity rules), the InApp ack action, and any future per-row
    /// dismiss flow.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the excursion (defence-in-depth check).</param>
    /// <param name="excursionId">The excursion whose instances should be acknowledged.</param>
    /// <param name="acknowledgedBy">
    /// Identifier of the user or system performing the acknowledgement. System
    /// callers use the <c>"system:&lt;reason&gt;"</c> convention so the audit
    /// trail can parse the source (e.g. <c>"system:auto-ack-on-trigger"</c>).
    /// </param>
    /// <param name="broadcast">
    /// When false, suppresses the <c>alert_acknowledged</c> SignalR broadcast — used by the
    /// auto-ack-on-trigger flow which immediately follows an <c>alert_dispatch</c> for the same
    /// excursion the FE has not yet rendered.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task AcknowledgeExcursionAsync(
        Guid tenantId,
        Guid excursionId,
        string acknowledgedBy,
        bool broadcast,
        CancellationToken ct);
}
