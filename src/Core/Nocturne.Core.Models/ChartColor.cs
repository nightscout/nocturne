using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Semantic color assignments for chart elements.
/// Values are kebab-case strings matching CSS custom property names,
/// so the frontend can resolve colors with: var(--{value})
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChartColor>))]
public enum ChartColor
{
    // Glucose ranges
    [EnumMember(Value = "glucose-very-low"), JsonStringEnumMemberName("glucose-very-low")]
    GlucoseVeryLow,

    [EnumMember(Value = "glucose-low"), JsonStringEnumMemberName("glucose-low")]
    GlucoseLow,

    [EnumMember(Value = "glucose-in-range"), JsonStringEnumMemberName("glucose-in-range")]
    GlucoseInRange,

    [EnumMember(Value = "glucose-high"), JsonStringEnumMemberName("glucose-high")]
    GlucoseHigh,

    [EnumMember(Value = "glucose-very-high"), JsonStringEnumMemberName("glucose-very-high")]
    GlucoseVeryHigh,

    // Insulin
    [EnumMember(Value = "insulin-bolus"), JsonStringEnumMemberName("insulin-bolus")]
    InsulinBolus,

    [EnumMember(Value = "insulin-basal"), JsonStringEnumMemberName("insulin-basal")]
    InsulinBasal,

    [EnumMember(Value = "insulin-temp-basal"), JsonStringEnumMemberName("insulin-temp-basal")]
    InsulinTempBasal,

    // Carbs
    [EnumMember(Value = "carbs"), JsonStringEnumMemberName("carbs")]
    Carbs,

    // Pump modes
    [EnumMember(Value = "pump-mode-automatic"), JsonStringEnumMemberName("pump-mode-automatic")]
    PumpModeAutomatic,

    [EnumMember(Value = "pump-mode-limited"), JsonStringEnumMemberName("pump-mode-limited")]
    PumpModeLimited,

    [EnumMember(Value = "pump-mode-manual"), JsonStringEnumMemberName("pump-mode-manual")]
    PumpModeManual,

    [EnumMember(Value = "pump-mode-boost"), JsonStringEnumMemberName("pump-mode-boost")]
    PumpModeBoost,

    [EnumMember(Value = "pump-mode-ease-off"), JsonStringEnumMemberName("pump-mode-ease-off")]
    PumpModeEaseOff,

    [EnumMember(Value = "pump-mode-sleep"), JsonStringEnumMemberName("pump-mode-sleep")]
    PumpModeSleep,

    [EnumMember(Value = "pump-mode-exercise"), JsonStringEnumMemberName("pump-mode-exercise")]
    PumpModeExercise,

    [EnumMember(Value = "pump-mode-suspended"), JsonStringEnumMemberName("pump-mode-suspended")]
    PumpModeSuspended,

    [EnumMember(Value = "pump-mode-off"), JsonStringEnumMemberName("pump-mode-off")]
    PumpModeOff,

    // System events
    [EnumMember(Value = "system-event-alarm"), JsonStringEnumMemberName("system-event-alarm")]
    SystemEventAlarm,

    [EnumMember(Value = "system-event-hazard"), JsonStringEnumMemberName("system-event-hazard")]
    SystemEventHazard,

    [EnumMember(Value = "system-event-warning"), JsonStringEnumMemberName("system-event-warning")]
    SystemEventWarning,

    [EnumMember(Value = "system-event-info"), JsonStringEnumMemberName("system-event-info")]
    SystemEventInfo,

    // Activities
    [EnumMember(Value = "activity-sleep"), JsonStringEnumMemberName("activity-sleep")]
    ActivitySleep,

    [EnumMember(Value = "activity-exercise"), JsonStringEnumMemberName("activity-exercise")]
    ActivityExercise,

    [EnumMember(Value = "activity-illness"), JsonStringEnumMemberName("activity-illness")]
    ActivityIllness,

    [EnumMember(Value = "activity-travel"), JsonStringEnumMemberName("activity-travel")]
    ActivityTravel,

    // Trackers
    [EnumMember(Value = "tracker-sensor"), JsonStringEnumMemberName("tracker-sensor")]
    TrackerSensor,

    [EnumMember(Value = "tracker-cannula"), JsonStringEnumMemberName("tracker-cannula")]
    TrackerCannula,

    [EnumMember(Value = "tracker-reservoir"), JsonStringEnumMemberName("tracker-reservoir")]
    TrackerReservoir,

    [EnumMember(Value = "tracker-battery"), JsonStringEnumMemberName("tracker-battery")]
    TrackerBattery,

    [EnumMember(Value = "tracker-consumable"), JsonStringEnumMemberName("tracker-consumable")]
    TrackerConsumable,

    [EnumMember(Value = "tracker-appointment"), JsonStringEnumMemberName("tracker-appointment")]
    TrackerAppointment,

    [EnumMember(Value = "tracker-reminder"), JsonStringEnumMemberName("tracker-reminder")]
    TrackerReminder,

    [EnumMember(Value = "tracker-custom"), JsonStringEnumMemberName("tracker-custom")]
    TrackerCustom,

    // Generic
    [EnumMember(Value = "chart-1"), JsonStringEnumMemberName("chart-1")]
    Profile,

    [EnumMember(Value = "chart-2"), JsonStringEnumMemberName("chart-2")]
    Override,

    [EnumMember(Value = "muted-foreground"), JsonStringEnumMemberName("muted-foreground")]
    MutedForeground,

    [EnumMember(Value = "primary"), JsonStringEnumMemberName("primary")]
    Primary,
}
