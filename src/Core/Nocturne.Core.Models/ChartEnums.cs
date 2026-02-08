using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Typed enum for device event types from Nightscout treatments.
/// Replaces magic string comparisons with a proper enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceEventType>))]
public enum DeviceEventType
{
    SensorStart,
    SensorChange,
    SensorStop,
    SiteChange,
    InsulinChange,
    PumpBatteryChange,
}

/// <summary>
/// Typed enum for bolus event types from Nightscout treatments.
/// Replaces magic string comparisons with a proper enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<BolusType>))]
public enum BolusType
{
    Bolus,
    MealBolus,
    CorrectionBolus,
    SnackBolus,
    BolusWizard,
    ComboBolus,
    Smb,
    AutomaticBolus,
}
