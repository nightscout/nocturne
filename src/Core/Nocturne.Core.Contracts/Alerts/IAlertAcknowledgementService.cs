using Nocturne.Core.Models.Alerts;

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
    /// The one acknowledgement decision for a single excursion. A caller whose authority includes
    /// <c>alerts.readwrite</c> acknowledges it for everyone: every unresolved instance is silenced and
    /// escalation stops, while the excursion stays open for hysteresis. Any other member mutes it for
    /// themselves: later deliveries skip that member's in-app notifications and registered client
    /// devices until the excursion closes, and nobody else's escalation changes.
    /// </summary>
    /// <remarks>
    /// Authority is the member behind the credential, not only the credential: a device grant carries
    /// <c>device.notify</c> but not <c>alerts.readwrite</c>, so a token that falls short is judged
    /// again on the subject's own membership, and an owner's Companion still acknowledges for
    /// everyone. <see cref="AlertAcknowledgementAuthority.System"/> always acknowledges for everyone.
    /// </remarks>
    /// <param name="tenantId">The tenant that owns the excursion (defence-in-depth check).</param>
    /// <param name="excursionId">The excursion to acknowledge.</param>
    /// <param name="acknowledgedBy">
    /// Identifier of the user or system performing the acknowledgement. System
    /// callers use the <c>"system:&lt;reason&gt;"</c> convention so the audit
    /// trail can parse the source (e.g. <c>"system:auto-ack-on-trigger"</c>).
    /// </param>
    /// <param name="caller">The authority the acknowledgement is made with.</param>
    /// <param name="broadcast">
    /// When false, suppresses the <c>alert_acknowledged</c> SignalR broadcast — used by the
    /// auto-ack-on-trigger flow which immediately follows an <c>alert_dispatch</c> for the same
    /// excursion the FE has not yet rendered.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Which outcome applied. An excursion someone already acknowledged reads as
    /// <see cref="AlertAcknowledgementOutcome.Acknowledged"/> whoever asks.
    /// </returns>
    Task<AlertAcknowledgementOutcome> AcknowledgeExcursionAsync(
        Guid tenantId,
        Guid excursionId,
        string acknowledgedBy,
        AlertAcknowledgementAuthority caller,
        bool broadcast,
        CancellationToken ct);
}
