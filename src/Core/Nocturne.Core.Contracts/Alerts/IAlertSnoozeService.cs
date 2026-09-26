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
    /// stands: excursion open and out of hysteresis, unacknowledged, rule enabled, not
    /// re-snoozed, and not silenced by DND. Returns whether a dispatch was made.
    /// </summary>
    Task<bool> ResumeAsync(Guid instanceId, CancellationToken ct);
}
