using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents the type of treatment event.
/// 1:1 compatibility with Nightscout event types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TreatmentEventType
{
    /// <summary>
    /// No specific event type
    /// </summary>
    [EnumMember(Value = "<none>")]
    None,

    /// <summary>
    /// Blood glucose check
    /// </summary>
    [EnumMember(Value = "BG Check")]
    BgCheck,

    /// <summary>
    /// Snack bolus
    /// </summary>
    [EnumMember(Value = "Snack Bolus")]
    SnackBolus,

    /// <summary>
    /// Meal bolus
    /// </summary>
    [EnumMember(Value = "Meal Bolus")]
    MealBolus,

    /// <summary>
    /// Correction bolus
    /// </summary>
    [EnumMember(Value = "Correction Bolus")]
    CorrectionBolus,

    /// <summary>
    /// Carbohydrate correction
    /// </summary>
    [EnumMember(Value = "Carb Correction")]
    CarbCorrection,

    /// <summary>
    /// Combo/extended bolus
    /// </summary>
    [EnumMember(Value = "Combo Bolus")]
    ComboBolus,

    /// <summary>
    /// Announcement
    /// </summary>
    [EnumMember(Value = "Announcement")]
    Announcement,

    /// <summary>
    /// Note
    /// </summary>
    [EnumMember(Value = "Note")]
    Note,

    /// <summary>
    /// Question
    /// </summary>
    [EnumMember(Value = "Question")]
    Question,

    /// <summary>
    /// Pump site change
    /// </summary>
    [EnumMember(Value = "Site Change")]
    SiteChange,

    /// <summary>
    /// CGM sensor start
    /// </summary>
    [EnumMember(Value = "Sensor Start")]
    SensorStart,

    /// <summary>
    /// CGM sensor insert/change
    /// </summary>
    [EnumMember(Value = "Sensor Change")]
    SensorChange,

    /// <summary>
    /// CGM sensor stop
    /// </summary>
    [EnumMember(Value = "Sensor Stop")]
    SensorStop,

    /// <summary>
    /// Pump battery change
    /// </summary>
    [EnumMember(Value = "Pump Battery Change")]
    PumpBatteryChange,

    /// <summary>
    /// Insulin cartridge change
    /// </summary>
    [EnumMember(Value = "Insulin Change")]
    InsulinChange,

    /// <summary>
    /// Temporary basal rate start
    /// </summary>
    [EnumMember(Value = "Temp Basal Start")]
    TempBasalStart,

    /// <summary>
    /// Temporary basal rate end
    /// </summary>
    [EnumMember(Value = "Temp Basal End")]
    TempBasalEnd,

    /// <summary>
    /// Profile switch
    /// </summary>
    [EnumMember(Value = "Profile Switch")]
    ProfileSwitch,

    /// <summary>
    /// Diabetes Alert Dog alert
    /// </summary>
    [EnumMember(Value = "D.A.D. Alert")]
    DadAlert,

    /// <summary>
    /// Temporary basal rate (generic)
    /// </summary>
    [EnumMember(Value = "Temp Basal")]
    TempBasal,

    /// <summary>
    /// Exercise event
    /// </summary>
    [EnumMember(Value = "Exercise")]
    Exercise,

    /// <summary>
    /// OpenAPS offline event
    /// </summary>
    [EnumMember(Value = "OpenAPS Offline")]
    OpenApsOffline,

    /// <summary>
    /// Suspend pump event
    /// </summary>
    [EnumMember(Value = "Suspend Pump")]
    SuspendPump,

    /// <summary>
    /// Resume pump event
    /// </summary>
    [EnumMember(Value = "Resume Pump")]
    ResumePump,

    /// <summary>
    /// Bolus wizard event
    /// </summary>
    [EnumMember(Value = "Bolus Wizard")]
    BolusWizard,

    /// <summary>
    /// Calibration event
    /// </summary>
    [EnumMember(Value = "Calibration")]
    Calibration,

    /// <summary>
    /// Transmitter sensor insert
    /// </summary>
    [EnumMember(Value = "Transmitter Sensor Insert")]
    TransmitterSensorInsert,

    /// <summary>
    /// Pod change
    /// </summary>
    [EnumMember(Value = "Pod Change")]
    PodChange,

    /// <summary>
    /// Temporary target (AAPS)
    /// </summary>
    [EnumMember(Value = "Temporary Target")]
    TemporaryTarget
}
