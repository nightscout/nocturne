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
    /// Re-dispatches a lapsed snooze's instance to its rule's channels when the alert still
    /// stands: excursion open and out of hysteresis, unacknowledged, rule enabled and evaluated (no
    /// stored value its engine cannot read), not re-snoozed, and not silenced by DND. Returns whether
    /// a dispatch was made.
    /// </summary>
    /// <remarks>
    /// The dispatch holds the excursion's opening owed (<see cref="IAlertRepository.OweOpeningAsync"/>)
    /// until its close has run again, so the sweep finishes a run that dies as an owed opening
    /// (<c>IExcursionOpenedHandler</c>). That closes an excursion that has ended, and sends only the
    /// channels with no answered delivery row, so it does not repeat the re-notification on a channel
    /// that answered, nor send again one the run lost there. An opening already owed keeps its stamp,
    /// and its work, for the sweep.
    /// </remarks>
    Task<bool> ResumeAsync(Guid instanceId, CancellationToken ct);
}
