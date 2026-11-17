using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Typed enum for device event types from Nightscout treatments.
/// Replaces magic string comparisons with a proper enum.
/// </summary>
/// <seealso cref="Treatment"/>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceEventType>))]
public enum DeviceEventType
{
    /// <summary>CGM sensor started</summary>
    SensorStart,

    /// <summary>CGM sensor changed or inserted</summary>
    SensorChange,

    /// <summary>CGM sensor stopped</summary>
    SensorStop,

    /// <summary>Pump infusion site changed</summary>
    SiteChange,

    /// <summary>Insulin cartridge changed</summary>
    InsulinChange,

    PumpBatteryChange,

    /// <summary>Omnipod pod changed</summary>
    PodChange,

    /// <summary>Insulin reservoir changed</summary>
    ReservoirChange,

    /// <summary>Pump cannula changed</summary>
    CannulaChange,

    /// <summary>CGM transmitter sensor inserted</summary>
    TransmitterSensorInsert,

    /// <summary>Omnipod pod activated</summary>
    PodActivated,

    /// <summary>Omnipod pod deactivated</summary>
    PodDeactivated,

    /// <summary>Pump insulin delivery suspended</summary>
    PumpSuspend,

    /// <summary>Pump insulin delivery resumed</summary>
    PumpResume,

    Priming,

    /// <summary>Pump tubing primed</summary>
    TubePriming,

    /// <summary>Pump needle/cannula primed</summary>
    NeedlePriming,

    /// <summary>Pump reservoir rewind</summary>
    Rewind,

    DateChanged,

    TimeChanged,

    /// <summary>Maximum bolus limit changed</summary>
    BolusMaxChanged,

    /// <summary>Maximum basal rate limit changed</summary>
    BasalMaxChanged,

    /// <summary>Active basal profile switched</summary>
    ProfileSwitch,
}

/// <summary>
/// Typed enum for bolus event types from Nightscout treatments.
/// Replaces magic string comparisons with a proper enum.
/// </summary>
/// <seealso cref="Treatment"/>
[JsonConverter(typeof(JsonStringEnumConverter<BolusType>))]
public enum BolusType
{
    /// <summary>Generic bolus with no specific meal context</summary>
    Bolus,

    MealBolus,

    /// <summary>Correction bolus to bring glucose into range</summary>
    CorrectionBolus,

    SnackBolus,

    /// <summary>Bolus delivered via the pump's built-in wizard</summary>
    BolusWizard,

    /// <summary>Combined immediate and extended (square-wave) bolus</summary>
    ComboBolus,

    /// <summary>Super Micro Bolus delivered automatically by an AID algorithm</summary>
    Smb,

    /// <summary>Any other automated bolus not categorized as SMB</summary>
    AutomaticBolus,
}

/// <summary>
/// Part of the patient's local day in which a meal was eaten.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MealPeriod>))]
public enum MealPeriod
{
    /// <summary>05:00 to 10:59 local time</summary>
    Breakfast,

    /// <summary>11:00 to 14:59 local time</summary>
    Lunch,

    /// <summary>15:00 to 16:59 local time</summary>
    Snack,

    /// <summary>17:00 to 20:59 local time</summary>
    Dinner,

    /// <summary>21:00 to 04:59 local time</summary>
    LateNight,
}
