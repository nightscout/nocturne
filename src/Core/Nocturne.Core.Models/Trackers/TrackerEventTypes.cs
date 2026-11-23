using Nocturne.Core.Constants;

namespace Nocturne.Core.Models;

/// <summary>
/// The Nightscout event names a tracker definition may name: the device events that restart or end
/// its runs, and the device events a manual start or completion records.
/// </summary>
public static class TrackerEventTypes
{
    /// <summary>
    /// The event types a definition may list in <see cref="TrackerDefinition.TriggerEventTypes"/>:
    /// the device events that mean something was changed. Pump suspend and resume are device events
    /// too, but pausing a pump ends nobody's sensor.
    /// </summary>
    public static readonly IReadOnlyList<string> TriggerableEventTypes =
    [
        TreatmentTypes.SensorStart,
        TreatmentTypes.SensorChange,
        TreatmentTypes.SensorStop,
        TreatmentTypes.TransmitterSensorInsert,
        TreatmentTypes.SiteChange,
        TreatmentTypes.CannulaChange,
        TreatmentTypes.PodChange,
        TreatmentTypes.PodActivated,
        TreatmentTypes.InsulinChange,
        TreatmentTypes.ReservoirChangeEvent,
        TreatmentTypes.PumpBatteryChange,
    ];

    /// <summary>
    /// The event types a definition may list in <see cref="TrackerDefinition.EndEventTypes"/>:
    /// the device events that mean something came off with nothing yet in its place.
    /// </summary>
    public static readonly IReadOnlyList<string> EndingEventTypes =
    [
        TreatmentTypes.SensorStop,
        TreatmentTypes.PodDeactivated,
    ];

    /// <summary>
    /// The event types a definition may record when a run starts or completes: the Nightscout names
    /// that map to a device event (<see cref="TreatmentTypeMaps.DeviceEventTypeMap"/>).
    /// </summary>
    public static IEnumerable<string> DeviceEventTypes => TreatmentTypeMaps.DeviceEventTypeMap.Keys;

    /// <summary>The <see cref="TriggerableEventTypes"/> spelling of <paramref name="name"/>, or null.</summary>
    public static string? CanonicalTrigger(string name) => Canonical(TriggerableEventTypes, name);

    /// <summary>The <see cref="EndingEventTypes"/> spelling of <paramref name="name"/>, or null.</summary>
    public static string? CanonicalEnd(string name) => Canonical(EndingEventTypes, name);

    private static string? Canonical(IReadOnlyList<string> allowed, string name) =>
        allowed.FirstOrDefault(t => string.Equals(t, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Canonicalises a requested trigger list against <see cref="TriggerableEventTypes"/>. Blank entries
    /// are dropped, so a form can post one empty value to mean "none"; an unknown name is an error
    /// rather than a trigger that silently never fires. Null passes through as "not supplied".
    /// </summary>
    public static bool TryNormaliseTriggers(
        IEnumerable<string>? requested,
        out List<string>? normalised,
        out string? error) =>
        TryNormalise(requested, CanonicalTrigger, "restart", out normalised, out error);

    /// <summary>As <see cref="TryNormaliseTriggers"/>, for an end list against <see cref="EndingEventTypes"/>.</summary>
    public static bool TryNormaliseEnds(
        IEnumerable<string>? requested,
        out List<string>? normalised,
        out string? error) =>
        TryNormalise(requested, CanonicalEnd, "end", out normalised, out error);

    private static bool TryNormalise(
        IEnumerable<string>? requested,
        Func<string, string?> canonicalise,
        string verb,
        out List<string>? normalised,
        out string? error)
    {
        normalised = null;
        error = null;
        if (requested is null)
            return true;

        normalised = [];
        foreach (var name in requested.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
        {
            var canonical = canonicalise(name);
            if (canonical is null)
            {
                error = $"'{name}' is not an event a tracker can {verb} on";
                return false;
            }
            if (!normalised.Contains(canonical))
                normalised.Add(canonical);
        }
        return true;
    }

    /// <summary>
    /// Canonicalises a requested event name against <see cref="DeviceEventTypes"/>. Blank is "not
    /// supplied"; a name with no device event is an error naming <paramref name="field"/>.
    /// </summary>
    public static bool TryNormaliseDeviceEvent(string? requested, string field, out string? normalised, out string? error)
    {
        normalised = null;
        error = null;
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        var name = requested.Trim();
        normalised = DeviceEventTypes
            .FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (normalised is null)
            error = $"{field} '{name}' is not a device event";
        return error is null;
    }
}
