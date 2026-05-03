using System.Runtime.Serialization;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The type of condition an alert rule evaluates.
/// Used as a discriminator to route rules to the correct IConditionEvaluator.
/// </summary>
/// <seealso cref="AlertRuleSeverity"/>
[JsonConverter(typeof(JsonStringEnumConverter<AlertConditionType>))]
public enum AlertConditionType
{
    /// <summary>Glucose value crosses above or below a fixed threshold.</summary>
    [EnumMember(Value = "threshold"), JsonStringEnumMemberName("threshold")]
    Threshold,

    /// <summary>Glucose is rising or falling faster than a configured rate (mg/dL per minute).</summary>
    [EnumMember(Value = "rate_of_change"), JsonStringEnumMemberName("rate_of_change")]
    RateOfChange,

    /// <summary>No CGM data received within the configured time window.</summary>
    [EnumMember(Value = "signal_loss"), JsonStringEnumMemberName("signal_loss")]
    SignalLoss,

    /// <summary>Logical combination of multiple child conditions (AND/OR).</summary>
    [EnumMember(Value = "composite"), JsonStringEnumMemberName("composite")]
    Composite,

    /// <summary>Logical negation of a child condition.</summary>
    [EnumMember(Value = "not"), JsonStringEnumMemberName("not")]
    Not,

    /// <summary>Child condition must remain true for a sustained duration before firing.</summary>
    [EnumMember(Value = "sustained"), JsonStringEnumMemberName("sustained")]
    Sustained,

    /// <summary>The most recent reading is older than the configured staleness threshold.</summary>
    [EnumMember(Value = "staleness"), JsonStringEnumMemberName("staleness")]
    Staleness,

    /// <summary>Predicted glucose crosses a threshold within a forecast horizon.</summary>
    [EnumMember(Value = "predicted"), JsonStringEnumMemberName("predicted")]
    Predicted,

    /// <summary>Glucose trend matches a configured direction bucket (e.g., rising fast).</summary>
    [EnumMember(Value = "trend"), JsonStringEnumMemberName("trend")]
    Trend,

    /// <summary>Current local time falls within a configured window.</summary>
    [EnumMember(Value = "time_of_day"), JsonStringEnumMemberName("time_of_day")]
    TimeOfDay,

    /// <summary>Insulin on board (units) compared against a threshold.</summary>
    [EnumMember(Value = "iob"), JsonStringEnumMemberName("iob")]
    Iob,

    /// <summary>Carbs on board (grams) compared against a threshold.</summary>
    [EnumMember(Value = "cob"), JsonStringEnumMemberName("cob")]
    Cob,

    /// <summary>Pump reservoir level (units) compared against a threshold.</summary>
    [EnumMember(Value = "reservoir"), JsonStringEnumMemberName("reservoir")]
    Reservoir,

    /// <summary>Days since last infusion site change compared against a threshold.</summary>
    [EnumMember(Value = "site_age"), JsonStringEnumMemberName("site_age")]
    SiteAge,

    /// <summary>Days since CGM sensor start compared against a threshold.</summary>
    [EnumMember(Value = "sensor_age"), JsonStringEnumMemberName("sensor_age")]
    SensorAge,

    /// <summary>Cross-references the live state of another alert (e.g., active for N minutes).</summary>
    [EnumMember(Value = "alert_state"), JsonStringEnumMemberName("alert_state")]
    AlertState,

    /// <summary>Minutes since the latest APS cycle (suggested or enacted) — loop liveness.</summary>
    [EnumMember(Value = "loop_stale"), JsonStringEnumMemberName("loop_stale")]
    LoopStale,

    /// <summary>Minutes since the latest enacted APS cycle — closed-loop enaction liveness.</summary>
    [EnumMember(Value = "loop_enaction_stale"), JsonStringEnumMemberName("loop_enaction_stale")]
    LoopEnactionStale,

    /// <summary>Pump suspension state, optionally for a sustained duration.</summary>
    [EnumMember(Value = "pump_suspended"), JsonStringEnumMemberName("pump_suspended")]
    PumpSuspended,

    /// <summary>Pump battery percent comparison.</summary>
    [EnumMember(Value = "pump_battery"), JsonStringEnumMemberName("pump_battery")]
    PumpBattery,

    /// <summary>Active temp basal rate (U/hr) or percent of scheduled comparison.</summary>
    [EnumMember(Value = "temp_basal"), JsonStringEnumMemberName("temp_basal")]
    TempBasal,

    /// <summary>Uploader (phone) battery percent comparison.</summary>
    [EnumMember(Value = "uploader_battery"), JsonStringEnumMemberName("uploader_battery")]
    UploaderBattery,

    /// <summary>Active override state, optionally for a sustained duration.</summary>
    [EnumMember(Value = "override_active"), JsonStringEnumMemberName("override_active")]
    OverrideActive,

    /// <summary>OpenAPS sensitivity ratio (autosens) comparison.</summary>
    [EnumMember(Value = "sensitivity_ratio"), JsonStringEnumMemberName("sensitivity_ratio")]
    SensitivityRatio,

    /// <summary>Tenant Do Not Disturb state, optionally for a sustained duration.</summary>
    [EnumMember(Value = "do_not_disturb"), JsonStringEnumMemberName("do_not_disturb")]
    DoNotDisturb,

    /// <summary>Glucose falls into one of a configured set of buckets (very_low/low/tight_range/in_range/high/very_high).</summary>
    [EnumMember(Value = "glucose_bucket"), JsonStringEnumMemberName("glucose_bucket")]
    GlucoseBucket,

    /// <summary>Minutes since the latest carb-bearing treatment compared against a threshold.</summary>
    [EnumMember(Value = "time_since_last_carb"), JsonStringEnumMemberName("time_since_last_carb")]
    TimeSinceLastCarb,

    /// <summary>Minutes since the latest insulin-bearing treatment compared against a threshold.</summary>
    [EnumMember(Value = "time_since_last_bolus"), JsonStringEnumMemberName("time_since_last_bolus")]
    TimeSinceLastBolus,

    /// <summary>Local day of week (in the tenant's timezone) matches one of a configured set.</summary>
    [EnumMember(Value = "day_of_week"), JsonStringEnumMemberName("day_of_week")]
    DayOfWeek,

    /// <summary>Pump operational mode (Automatic/Manual/Boost/etc.) state, optionally for a sustained duration.</summary>
    [EnumMember(Value = "pump_state"), JsonStringEnumMemberName("pump_state")]
    PumpState,

    /// <summary>Generic state-span-active leaf for non-pump-mode categories (Override, Sleep, Exercise, ...).</summary>
    [EnumMember(Value = "state_span_active"), JsonStringEnumMemberName("state_span_active")]
    StateSpanActive,
}
