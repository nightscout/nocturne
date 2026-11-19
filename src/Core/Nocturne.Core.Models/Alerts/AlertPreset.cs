using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>Where a preset is listed: with the types most people start from, or behind them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertPresetGroup>))]
public enum AlertPresetGroup
{
    Common,
    More,
}

/// <summary>
/// A ready-made alert: one condition with a fixed comparison, so a person sets a number and never
/// an operator. The alert wizard offers every preset, and the setup hub starts a tenant with the
/// ones that have a <see cref="StarterKind"/>. A preset is where a rule starts and nothing ties a
/// rule to it afterwards.
/// </summary>
/// <param name="Id">The preset's stable key; the wizard has its name and description under it.</param>
/// <param name="CanWait">Whether the condition can be held for a time before the alert starts. A
/// condition with its own duration cannot.</param>
/// <param name="ConditionType">Null for a preset that reads only a tracker.</param>
/// <param name="ConditionParams">The condition's payload as JSON, with the preset's default level.</param>
/// <param name="AutoResolve">Whether the alert closes at once when the opposite of its condition
/// is true, rather than on the evaluation after the one that found the condition false.</param>
public sealed record AlertPreset(
    string Id,
    AlertPresetGroup Group,
    AlertRuleSeverity Severity,
    bool CanWait,
    AlertConditionType? ConditionType,
    string? ConditionParams,
    bool AutoResolve)
{
    /// <summary>
    /// The tracker categories that measure the age this preset alerts on. With a tracker of one of
    /// them the alert reads that tracker (<see cref="AlertConditionType.TrackerAge"/>) at its
    /// lifespan; the preset's own condition is what it reads with none.
    /// </summary>
    public IReadOnlyList<TrackerCategory> TrackerCategories { get; init; } = [];

    /// <summary>
    /// The age, in hours, the alert starts at on a tracker with no lifespan. Set on a preset with no
    /// condition of its own, which is offered only when a tracker of its categories exists.
    /// </summary>
    public int? TrackerOnlyAgeHours { get; init; }

    /// <summary>The setup hub's starter rule this preset is, when it is one.</summary>
    public StarterAlertKind? StarterKind { get; init; }
}

/// <summary>The one list of ready-made alerts, in the order they are offered.</summary>
public static class AlertPresets
{
    public static IReadOnlyList<AlertPreset> All { get; } =
    [
        new("urgent_low", AlertPresetGroup.Common, AlertRuleSeverity.Critical, CanWait: true,
            AlertConditionType.Threshold, """{"direction":"below","value":55}""", AutoResolve: true)
        {
            StarterKind = StarterAlertKind.UrgentLow,
        },
        new("low", AlertPresetGroup.Common, AlertRuleSeverity.Warning, CanWait: true,
            AlertConditionType.Threshold, """{"direction":"below","value":70}""", AutoResolve: true)
        {
            StarterKind = StarterAlertKind.Low,
        },
        new("high", AlertPresetGroup.Common, AlertRuleSeverity.Warning, CanWait: true,
            AlertConditionType.Threshold, """{"direction":"above","value":250}""", AutoResolve: true)
        {
            StarterKind = StarterAlertKind.High,
        },
        // A rate has no condition that is its opposite. The nearest, a flat trend, is true together
        // with a rate set below the edge of flat, and would close the alert as it starts.
        new("falling_fast", AlertPresetGroup.Common, AlertRuleSeverity.Warning, CanWait: true,
            AlertConditionType.RateOfChange, """{"direction":"falling","rate":3}""", AutoResolve: false),
        new("rising_fast", AlertPresetGroup.Common, AlertRuleSeverity.Info, CanWait: true,
            AlertConditionType.RateOfChange, """{"direction":"rising","rate":3}""", AutoResolve: false),
        // No reading says the sensor is back: the condition going false is the only sign of it.
        new("no_readings", AlertPresetGroup.Common, AlertRuleSeverity.Warning, CanWait: false,
            AlertConditionType.SignalLoss, """{"timeout_minutes":20}""", AutoResolve: false)
        {
            StarterKind = StarterAlertKind.NoReadings,
        },
        // A forecast is true when any of its points matches, so a falling one holds a point at or
        // below the level and a point above it: the flipped comparison is not the opposite, and
        // would close the alert on the evaluation that starts it.
        new("urgent_low_soon", AlertPresetGroup.More, AlertRuleSeverity.Critical, CanWait: false,
            AlertConditionType.Predicted, """{"operator":"<=","value":55,"within_minutes":20}""", AutoResolve: false),
        new("reservoir_low", AlertPresetGroup.More, AlertRuleSeverity.Warning, CanWait: false,
            AlertConditionType.Reservoir, """{"operator":"<=","value":20}""", AutoResolve: true),
        new("pump_battery_low", AlertPresetGroup.More, AlertRuleSeverity.Warning, CanWait: false,
            AlertConditionType.PumpBattery, """{"operator":"<=","value":20}""", AutoResolve: true),
        new("battery_change_due", AlertPresetGroup.More, AlertRuleSeverity.Info, CanWait: false,
            ConditionType: null, ConditionParams: null, AutoResolve: true)
        {
            TrackerCategories = [TrackerCategory.Battery],
            TrackerOnlyAgeHours = 21 * 24,
        },
        new("site_change_due", AlertPresetGroup.More, AlertRuleSeverity.Info, CanWait: false,
            AlertConditionType.SiteAge, """{"operator":">=","value":72}""", AutoResolve: true)
        {
            TrackerCategories = [TrackerCategory.Cannula, TrackerCategory.Pod],
        },
        new("sensor_change_due", AlertPresetGroup.More, AlertRuleSeverity.Info, CanWait: false,
            AlertConditionType.SensorAge, """{"operator":">=","value":10}""", AutoResolve: true)
        {
            TrackerCategories = [TrackerCategory.Sensor],
        },
        // As for no readings: nothing but the loop running again says the alert is over.
        new("loop_not_running", AlertPresetGroup.More, AlertRuleSeverity.Warning, CanWait: false,
            AlertConditionType.LoopStale, """{"operator":">","minutes":30}""", AutoResolve: false),
        new("phone_battery_low", AlertPresetGroup.More, AlertRuleSeverity.Info, CanWait: false,
            AlertConditionType.UploaderBattery, """{"operator":"<=","value":20}""", AutoResolve: true),
    ];

    /// <summary>The preset the setup hub starts a tenant with for <paramref name="kind"/>.</summary>
    public static AlertPreset Starter(StarterAlertKind kind) => All.Single(p => p.StarterKind == kind);
}
