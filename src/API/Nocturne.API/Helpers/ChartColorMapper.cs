using Nocturne.Core.Models;

namespace Nocturne.API.Helpers;

/// <summary>
/// Maps domain enums to ChartColor values for chart visualization.
/// Single source of truth for all chart color assignments.
/// </summary>
public static class ChartColorMapper
{
    public static ChartColor FromPumpMode(string state) =>
        state switch
        {
            "Automatic" => ChartColor.PumpModeAutomatic,
            "Limited" => ChartColor.PumpModeLimited,
            "Manual" => ChartColor.PumpModeManual,
            "Boost" => ChartColor.PumpModeBoost,
            "EaseOff" => ChartColor.PumpModeEaseOff,
            "Sleep" => ChartColor.PumpModeSleep,
            "Exercise" => ChartColor.PumpModeExercise,
            "Suspended" => ChartColor.PumpModeSuspended,
            "Off" => ChartColor.PumpModeOff,
            _ => ChartColor.PumpModeManual,
        };

    public static ChartColor FromSystemEvent(SystemEventType type) =>
        type switch
        {
            SystemEventType.Alarm => ChartColor.SystemEventAlarm,
            SystemEventType.Hazard => ChartColor.SystemEventHazard,
            SystemEventType.Warning => ChartColor.SystemEventWarning,
            SystemEventType.Info => ChartColor.SystemEventInfo,
            _ => ChartColor.SystemEventInfo,
        };

    public static ChartColor FromDeviceEvent(DeviceEventType type) =>
        type switch
        {
            DeviceEventType.SensorStart or DeviceEventType.SensorChange =>
                ChartColor.GlucoseInRange,
            DeviceEventType.SensorStop => ChartColor.GlucoseLow,
            DeviceEventType.SiteChange => ChartColor.InsulinBolus,
            DeviceEventType.InsulinChange => ChartColor.InsulinBasal,
            DeviceEventType.PumpBatteryChange => ChartColor.Carbs,
            _ => ChartColor.MutedForeground,
        };

    public static ChartColor FromTracker(TrackerCategory category) =>
        category switch
        {
            TrackerCategory.Sensor => ChartColor.TrackerSensor,
            TrackerCategory.Cannula => ChartColor.TrackerCannula,
            TrackerCategory.Reservoir => ChartColor.TrackerReservoir,
            TrackerCategory.Battery => ChartColor.TrackerBattery,
            TrackerCategory.Consumable => ChartColor.TrackerConsumable,
            TrackerCategory.Appointment => ChartColor.TrackerAppointment,
            TrackerCategory.Reminder => ChartColor.TrackerReminder,
            TrackerCategory.Custom => ChartColor.TrackerCustom,
            _ => ChartColor.MutedForeground,
        };

    public static ChartColor FromActivity(StateSpanCategory category) =>
        category switch
        {
            StateSpanCategory.Sleep => ChartColor.ActivitySleep,
            StateSpanCategory.Exercise => ChartColor.ActivityExercise,
            StateSpanCategory.Illness => ChartColor.ActivityIllness,
            StateSpanCategory.Travel => ChartColor.ActivityTravel,
            _ => ChartColor.MutedForeground,
        };

    public static ChartColor FromOverride(string state) =>
        state switch
        {
            "Boost" => ChartColor.PumpModeBoost,
            "Exercise" => ChartColor.PumpModeExercise,
            "Sleep" => ChartColor.PumpModeSleep,
            "EaseOff" => ChartColor.PumpModeEaseOff,
            _ => ChartColor.Override,
        };

    public static ChartColor FillFromBasalOrigin(BasalDeliveryOrigin origin) =>
        origin switch
        {
            BasalDeliveryOrigin.Algorithm => ChartColor.InsulinBasal,
            BasalDeliveryOrigin.Manual => ChartColor.InsulinTempBasal,
            BasalDeliveryOrigin.Suspended => ChartColor.PumpModeSuspended,
            BasalDeliveryOrigin.Inferred => ChartColor.InsulinBasal,
            _ => ChartColor.InsulinBasal,
        };

    public static ChartColor StrokeFromBasalOrigin(BasalDeliveryOrigin origin) =>
        origin switch
        {
            BasalDeliveryOrigin.Algorithm => ChartColor.InsulinBolus,
            BasalDeliveryOrigin.Manual => ChartColor.InsulinBolus,
            BasalDeliveryOrigin.Suspended => ChartColor.PumpModeSuspended,
            BasalDeliveryOrigin.Inferred => ChartColor.InsulinBasal,
            _ => ChartColor.InsulinBasal,
        };
}
