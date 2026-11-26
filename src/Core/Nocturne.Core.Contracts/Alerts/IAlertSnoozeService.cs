using Nocturne.Core.Models.Alerts;

namespace Nocturne.Core.Contracts.Alerts;

/// <summary>
/// Snoozes alert instances and re-arms them when the snooze lapses. Semantics are defined on
/// <see cref="AlertSnooze"/>.
/// </summary>
public interface IAlertSnoozeService
{
    /// <summary>
    /// Silences <paramref name="instanceId"/> for <paramref name="minutes"/> from now, replacing
    /// any snooze already in force, and withdraws its deliveries still waiting to be sent.
    /// </summary>
    Task<SnoozeOutcome> SnoozeAsync(Guid instanceId, int minutes, CancellationToken ct);

    /// <summary>
    /// Ends the lapsed snooze of <paramref name="instanceId"/>, and re-dispatches the instance to its
    /// rule's channels when the alert still stands: excursion open and out of hysteresis,
    /// unacknowledged, rule enabled and evaluated (no stored value its engine cannot read), not
    /// re-snoozed, and not silenced by DND. Returns whether a dispatch was made.
    /// </summary>
    /// <remarks>
    /// The snooze ends in the write that starts the re-notification and holds the excursion's opening
    /// owed (<see cref="IAlertRepository.EndLapsedSnoozeAsync"/>), until its close has run again. A
    /// run that dies before that write leaves the snooze lapsed, so the next sweep resumes it again; a
    /// run that dies after it is finished by the sweep as an owed opening (<c>IExcursionOpenedHandler</c>),
    /// which closes an excursion that has ended and sends what the re-notification still owes
    /// (<see cref="IAlertDeliveryService.DispatchAsync"/>). An opening already owed keeps its stamp,
    /// and its work, for the sweep.
    /// </remarks>
    Task<bool> ResumeAsync(Guid instanceId, CancellationToken ct);
}
