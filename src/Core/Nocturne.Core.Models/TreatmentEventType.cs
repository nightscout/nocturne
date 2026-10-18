using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Type of treatment event.
/// 1:1 compatibility with Nightscout event types.
/// <para>
/// The <see cref="EnumMemberAttribute"/> values on each member define the exact string representation
/// used by the Nightscout API (e.g., <c>"Meal Bolus"</c>, <c>"Site Change"</c>).
/// </para>
/// </summary>
/// <seealso cref="Treatment"/>
/// <seealso cref="EventTypeConfiguration"/>
/// <seealso cref="EventTypeConfigurations"/>
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

    [EnumMember(Value = "Snack Bolus")]
    SnackBolus,

    [EnumMember(Value = "Meal Bolus")]
    MealBolus,

    [EnumMember(Value = "Correction Bolus")]
    CorrectionBolus,

    [EnumMember(Value = "Carb Correction")]
    CarbCorrection,

    /// <summary>
    /// Combo/extended bolus
    /// </summary>
    [EnumMember(Value = "Combo Bolus")]
    ComboBolus,

    [EnumMember(Value = "Announcement")]
    Announcement,

    [EnumMember(Value = "Note")]
    Note,

    [EnumMember(Value = "Question")]
    Question,

    [EnumMember(Value = "Site Change")]
    SiteChange,

    [EnumMember(Value = "Sensor Start")]
    SensorStart,

    /// <summary>
    /// CGM sensor insert/change
    /// </summary>
    [EnumMember(Value = "Sensor Change")]
    SensorChange,

    [EnumMember(Value = "Sensor Stop")]
    SensorStop,

    [EnumMember(Value = "Pump Battery Change")]
    PumpBatteryChange,

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

    [EnumMember(Value = "Exercise")]
    Exercise,

    [EnumMember(Value = "OpenAPS Offline")]
    OpenApsOffline,

    [EnumMember(Value = "Suspend Pump")]
    SuspendPump,

    [EnumMember(Value = "Resume Pump")]
    ResumePump,

    [EnumMember(Value = "Bolus Wizard")]
    BolusWizard,

    [EnumMember(Value = "Calibration")]
    Calibration,

    [EnumMember(Value = "Transmitter Sensor Insert")]
    TransmitterSensorInsert,

    [EnumMember(Value = "Pod Change")]
    PodChange,

    /// <summary>
    /// Temporary target (AAPS)
    /// </summary>
    [EnumMember(Value = "Temporary Target")]
    TemporaryTarget
}
