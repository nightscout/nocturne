using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Monitoring;

/// <summary>When a legacy age has a fallback: what covers it, and when its events are trusted.</summary>
public static class LegacyFallbackRules
{
    /// <summary>
    /// A latest event older than this many lifespans is from a source that has probably stopped
    /// reporting it, not a consumable still worn.
    /// </summary>
    public const int StaleAfterLifespans = 3;

    /// <summary>The span <see cref="MinRepeatedEvents"/> is counted over.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromDays(120);

    /// <summary>
    /// The events a <see cref="RequiresRepeatedEvents"/> kind needs within <see cref="RepeatWindow"/>.
    /// </summary>
    public const int MinRepeatedEvents = 2;

    /// <summary>
    /// Whether one event of <paramref name="kind"/> is too little to go on. Few sources log battery
    /// changes, so a single one is as likely a one-off note as a habit.
    /// </summary>
    public static bool RequiresRepeatedEvents(LegacyConsumableKind kind) =>
        kind == LegacyConsumableKind.PumpBattery;

    /// <summary>Whether any of <paramref name="definitions"/> <see cref="Covers"/> <paramref name="kind"/>.</summary>
    public static bool IsCovered(LegacyConsumableKind kind, IEnumerable<TrackerDefinitionEntity> definitions) =>
        definitions.Any(d => Covers(kind, d));

    /// <summary>
    /// Whether <paramref name="definition"/> already stands for <paramref name="kind"/>: a Duration
    /// tracker restarted by one of its events, whatever its category, dashboard visibility or notes
    /// filter, or a Duration sensor, cannula or reservoir tracker of the kind's category that
    /// restarts on nothing, run by hand. A definition hidden from the dashboard still covers the
    /// kind; the member chose not to see it.
    /// </summary>
    /// <remarks>
    /// The battery category is not enough on its own: it also holds phone and CGM transmitter
    /// batteries, which stand for no pump battery.
    /// </remarks>
    public static bool Covers(LegacyConsumableKind kind, TrackerDefinitionEntity definition)
    {
        if (definition.Mode != TrackerMode.Duration)
            return false;

        var triggers = TrackerTriggerService.ParseTriggerEventTypes(definition.TriggerEventTypes);
        if (triggers.Count > 0)
            return ConsumableTrackerSpecs.EventTypes(kind).Any(triggers.Contains);

        return kind != LegacyConsumableKind.PumpBattery
            && definition.Category == ConsumableTrackerSpecs.CategoryOf(kind);
    }

    /// <summary>
    /// Whether <paramref name="definition"/> already does <paramref name="spec"/>'s job: it
    /// <see cref="Covers"/> the kind and, for a site, follows the same hardware (pod or infusion set).
    /// </summary>
    public static bool Follows(TrackerDefinitionEntity definition, ConsumableTrackerSpec spec)
    {
        if (ConsumableTrackerSpecs.KindOf(spec) is not { } kind || !Covers(kind, definition))
            return false;
        if (kind != LegacyConsumableKind.Site)
            return true;

        var triggers = TrackerTriggerService.ParseTriggerEventTypes(definition.TriggerEventTypes);
        if (triggers.Count == 0)
            return true;

        var specTriggers = ConsumableTrackerSpecs.TriggerEventTypes(spec);
        return specTriggers.Contains(DeviceEventType.PodChange)
            ? triggers.Contains(DeviceEventType.PodChange)
            : !triggers.Contains(DeviceEventType.PodChange) || specTriggers.All(triggers.Contains);
    }

    /// <summary>
    /// Whether the latest event of <paramref name="kind"/>, at <paramref name="latestAt"/>, is
    /// trusted as the age of a consumable still worn.
    /// </summary>
    /// <param name="recentCount">Events of the kind within <see cref="RepeatWindow"/>; read only
    /// for a kind that <see cref="RequiresRepeatedEvents"/>.</param>
    public static bool IsConfident(
        LegacyConsumableKind kind,
        ConsumableTrackerSpec spec,
        DateTime latestAt,
        int recentCount,
        DateTime now)
    {
        if (IsStale(spec, latestAt, now))
            return false;

        return !RequiresRepeatedEvents(kind) || recentCount >= MinRepeatedEvents;
    }

    /// <summary>Whether an event at <paramref name="latestAt"/> is older than <see cref="StaleAfterLifespans"/> lifespans.</summary>
    public static bool IsStale(ConsumableTrackerSpec spec, DateTime latestAt, DateTime now) =>
        now - latestAt > TimeSpan.FromHours((double)spec.LifespanHours * StaleAfterLifespans);
}
