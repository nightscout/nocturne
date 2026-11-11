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
    /// tracker whose hardware provides the kind's capability (<see cref="TrackerCoverage.Of(TrackerDefinitionEntity)"/>), or,
    /// when that is not known, one restarted by one of the kind's events, whatever its category,
    /// dashboard visibility or notes filter, or a Duration sensor, cannula or reservoir tracker of
    /// the kind's category that restarts on nothing, run by hand. A definition hidden from the
    /// dashboard still covers the kind; the member chose not to see it.
    /// </summary>
    /// <remarks>
    /// The battery category is not enough on its own: it also holds phone and CGM transmitter
    /// batteries, which stand for no pump battery.
    /// </remarks>
    public static bool Covers(LegacyConsumableKind kind, TrackerDefinitionEntity definition)
    {
        if (definition.Mode != TrackerMode.Duration)
            return false;

        var covers = TrackerCoverage.Of(definition);
        if (covers.Count > 0 && ConsumableTrackerSpecs.CapabilityOf(kind) is { } capability)
            return covers.Contains(capability);

        var triggers = TrackerTriggerService.ParseTriggerEventTypes(definition.TriggerEventTypes);
        if (triggers.Count > 0)
            return ConsumableTrackerSpecs.EventTypes(kind).Any(triggers.Contains);

        return kind != LegacyConsumableKind.PumpBattery
            && definition.Category == ConsumableTrackerSpecs.CategoryOf(kind);
    }

    /// <summary>
    /// Whether <paramref name="definition"/> already does <paramref name="spec"/>'s job: it follows
    /// the same catalog part; or it <see cref="Covers"/> the spec's kind and restarts on every event
    /// the spec does; or it covers exactly what the spec's hardware provides; or, when either's
    /// coverage is not known, it covers the kind and, for a site, follows the same hardware (pod or
    /// infusion set).
    /// </summary>
    /// <remarks>
    /// Equal coverage is enough across models, so switching between two pod pumps keeps the pod
    /// tracker, while switching between a pod and an infusion set adds the new one.
    /// </remarks>
    public static bool Follows(TrackerDefinitionEntity definition, ConsumableTrackerSpec spec)
    {
        if (definition.Mode != TrackerMode.Duration)
            return false;
        if (spec.Component is not null && definition.DeviceComponent == spec.Component)
            return true;

        var kind = ConsumableTrackerSpecs.KindOf(spec);
        var triggers = TrackerTriggerService.ParseTriggerEventTypes(definition.TriggerEventTypes);
        var specTriggers = ConsumableTrackerSpecs.TriggerEventTypes(spec);
        if (kind is { } restarted && Covers(restarted, definition)
            && specTriggers.Count > 0 && specTriggers.All(triggers.Contains))
            return true;

        var covers = TrackerCoverage.Of(definition);
        if (covers.Count > 0 && spec.Covers is { Count: > 0 } specCovers)
            return covers.SetEquals(specCovers);

        if (kind is null || !Covers(kind.Value, definition))
            return false;
        if (kind != LegacyConsumableKind.Site)
            return true;

        if (triggers.Count == 0)
            return true;

        return specTriggers.Contains(DeviceEventType.PodChange)
            ? triggers.Contains(DeviceEventType.PodChange)
            : !triggers.Contains(DeviceEventType.PodChange) || specTriggers.All(triggers.Contains);
    }

    /// <summary>
    /// Whether the latest event of <paramref name="kind"/>, at <paramref name="latestAt"/>, is
    /// trusted as the age of a consumable still worn.
    /// </summary>
    /// <param name="kind">The consumable the event is of; decides whether repeated events are required.</param>
    /// <param name="spec">The tracker whose lifespan decides when the event is stale.</param>
    /// <param name="latestAt">When the latest event of the kind happened.</param>
    /// <param name="recentCount">Events of the kind within <see cref="RepeatWindow"/>; read only
    /// for a kind that <see cref="RequiresRepeatedEvents"/>.</param>
    /// <param name="now">The moment the event's age is measured from.</param>
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
