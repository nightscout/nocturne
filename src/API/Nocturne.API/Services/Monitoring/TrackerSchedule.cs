using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>
/// The one reading of a tracker's timeline: when each notification threshold fires and why a run
/// that ended was over. The alert rules <see cref="TrackerAlertRuleSyncService"/> synthesises and
/// the level a client shows for a running instance both come from here, so a pill can never light
/// at a different moment from the alert it stands for.
/// </summary>
public static class TrackerSchedule
{
    /// <summary>
    /// Minutes from the instance's reference time (start for Duration, scheduled time for Event) at
    /// which a threshold fires. Event thresholds are relative to the scheduled time, negative meaning
    /// before; Duration thresholds are relative to the start, and a negative one means that many
    /// hours before the lifespan ends. Null when a negative Duration threshold has no lifespan to
    /// count back from.
    /// </summary>
    public static int? OffsetMinutes(TrackerMode mode, int? lifespanHours, int thresholdHours)
    {
        if (mode == TrackerMode.Event || thresholdHours >= 0)
            return thresholdHours * 60;

        if (lifespanHours is not { } lifespan)
            return null;

        return (lifespan + thresholdHours) * 60;
    }

    /// <summary>
    /// The instant <paramref name="threshold"/> fires for <paramref name="instance"/>, or null when it
    /// never can: a misconfigured negative threshold, or an Event instance with no scheduled time.
    /// Requires <see cref="TrackerInstanceEntity.Definition"/> to be loaded.
    /// </summary>
    public static DateTime? FiresAt(TrackerInstanceEntity instance, TrackerNotificationThresholdEntity threshold)
    {
        var definition = instance.Definition;
        var reference = definition.Mode == TrackerMode.Event ? instance.ScheduledAt : instance.StartedAt;
        if (reference is not { } referenceAt)
            return null;

        return OffsetMinutes(definition.Mode, definition.LifespanHours, threshold.Hours) is { } minutes
            ? referenceAt.AddMinutes(minutes)
            : null;
    }

    /// <summary>
    /// Why a run that was ended by a newer one is over, as the history reads it: a run that reached its
    /// lifespan expired, one replaced before that was replaced early, and one with no lifespan has no
    /// term to have reached, so it is a plain completion.
    /// </summary>
    public static CompletionReason ReasonForReplacement(int? lifespanHours, DateTime startedAt, DateTime endedAt)
    {
        if (lifespanHours is not { } lifespan || lifespan <= 0)
            return CompletionReason.Completed;

        return endedAt - startedAt >= TimeSpan.FromHours(lifespan)
            ? CompletionReason.Expired
            : CompletionReason.ReplacedEarly;
    }
}
