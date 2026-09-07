using System.Text.Json.Serialization;

namespace Nocturne.Connectors.Glooko.Models;

/// <summary>
///     Cursor/pagination envelope shared by every SSV2 sync resource. Each resource response carries
///     its records under a resource-specific property (e.g. <c>egvs</c>) plus these three fields, which
///     drive incremental, ordered pagination: feed <see cref="LastUpdatedAt"/> + <see cref="LastGuid"/>
///     back into the next request until <see cref="LastPage"/> is <c>true</c>.
/// </summary>
public abstract class GlookoSsv2Page
{
    [JsonPropertyName("lastPage")] public bool LastPage { get; set; }

    [JsonPropertyName("lastUpdatedAt")] public string? LastUpdatedAt { get; set; }

    [JsonPropertyName("lastGuid")] public string? LastGuid { get; set; }
}

/// <summary>
///     A page of <c>/api/v2/cgm/egvs</c> — the granular per-reading CGM stream the Glooko mobile app
///     consumes (the SSV2 counterpart to the web flow's binned <c>cgm/readings</c>).
/// </summary>
public class GlookoEgvPage : GlookoSsv2Page
{
    [JsonPropertyName("egvs")] public GlookoEgv[]? Egvs { get; set; }
}

// ── Page wrappers reusing the existing v2 record models ─────────────────────
// These SSV2 resources are the same endpoints the windowed v2 path already calls, so their record
// shapes (and mappers) are unchanged; only the page envelope differs.

/// <summary>A page of <c>/api/v2/pumps/normal_boluses</c>.</summary>
public class GlookoNormalBolusPage : GlookoSsv2Page
{
    [JsonPropertyName("normalBoluses")] public GlookoBolus[]? NormalBoluses { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/scheduled_basals</c>.</summary>
public class GlookoScheduledBasalPage : GlookoSsv2Page
{
    [JsonPropertyName("scheduledBasals")] public GlookoBasal[]? ScheduledBasals { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/temporary_basals</c>.</summary>
public class GlookoTemporaryBasalPage : GlookoSsv2Page
{
    [JsonPropertyName("temporaryBasals")] public GlookoTempBasal[]? TemporaryBasals { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/suspend_basals</c>.</summary>
public class GlookoSuspendBasalPage : GlookoSsv2Page
{
    [JsonPropertyName("suspendBasals")] public GlookoSuspendBasal[]? SuspendBasals { get; set; }
}

/// <summary>A page of <c>/api/v2/readings</c> (manual meter readings → BG checks).</summary>
public class GlookoMeterReadingPage : GlookoSsv2Page
{
    [JsonPropertyName("readings")] public GlookoMeterReading[]? Readings { get; set; }
}

/// <summary>A page of <c>/api/v2/foods</c>.</summary>
public class GlookoFoodPage : GlookoSsv2Page
{
    [JsonPropertyName("foods")] public GlookoFood[]? Foods { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/events</c> (device events: reservoir/site/cannula changes, etc.).</summary>
public class GlookoPumpEventPage : GlookoSsv2Page
{
    [JsonPropertyName("events")] public GlookoPumpEvent[]? Events { get; set; }
}

/// <summary>
///     A single pump device event from the SSV2 <c>pumps/events</c> feed. <see cref="Type"/> is a
///     snake_case event kind (e.g. <c>reservoir_change</c>) mapped to a strongly-typed DeviceEventType.
/// </summary>
public class GlookoPumpEvent
{
    [JsonPropertyName("pumpTimestamp")] public string? PumpTimestamp { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("pumpGuid")] public string? PumpGuid { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>
///     A single estimated glucose value from the SSV2 egvs feed.
/// </summary>
public class GlookoEgv
{
    /// <summary>
    ///     Local display wall-clock (fake-UTC, like every other Glooko timestamp). This is the reading's
    ///     clinical time; <see cref="SystemTime"/> is the sensor's own clock and is not timezone-corrected.
    /// </summary>
    [JsonPropertyName("displayTime")] public string? DisplayTime { get; set; }

    [JsonPropertyName("systemTime")] public string? SystemTime { get; set; }

    /// <summary>
    ///     Glucose in mg/dL × 100 (integer encoding for 2-decimal precision), always mg/dL regardless of
    ///     the account's display units — same encoding as the v2 <c>cgm/readings</c> feed.
    /// </summary>
    [JsonPropertyName("glucoseValue")] public double GlucoseValue { get; set; }

    [JsonPropertyName("trendArrow")] public string? TrendArrow { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("cgmDeviceGuid")] public string? CgmDeviceGuid { get; set; }

    /// <summary>
    ///     True when the value was interpolated/back-filled rather than measured; excluded from ingest,
    ///     matching the v3 graph path which drops calculated readings.
    /// </summary>
    [JsonPropertyName("calculated")] public bool Calculated { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/injection_boluses</c> (manual pen-injected boluses).</summary>
public class GlookoInjectionBolusPage : GlookoSsv2Page
{
    [JsonPropertyName("injectionBoluses")] public GlookoInjectionInsulin[]? InjectionBoluses { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/injection_basals</c> (manual pen-injected long-acting basal).</summary>
public class GlookoInjectionBasalPage : GlookoSsv2Page
{
    [JsonPropertyName("injectionBasals")] public GlookoInjectionInsulin[]? InjectionBasals { get; set; }
}

/// <summary>
///     A manual pen injection (bolus or basal) from the SSV2 <c>pumps/injection_*</c> feeds. Shares the
///     pump-object envelope (<c>pumpTimestamp</c>/<c>guid</c>/<c>softDeleted</c>) with <see cref="GlookoBolus"/>
///     — both extend the app's GKPumpObject; <see cref="Name"/> is the insulin product name (e.g.
///     "Tresiba®U100"), resolved against the insulin catalog for DIA/peak. The SSV2 counterpart to the
///     v3 graph's <c>gkInsulinBolus</c>/<c>gkInsulinBasal</c> series.
/// </summary>
public class GlookoInjectionInsulin
{
    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("insulinDelivered")] public double InsulinDelivered { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/alarms</c> (pump alarms → system events).</summary>
public class GlookoSsv2AlarmPage : GlookoSsv2Page
{
    [JsonPropertyName("alarms")] public GlookoSsv2Alarm[]? Alarms { get; set; }
}

/// <summary>
///     A pump alarm from the SSV2 <c>pumps/alarms</c> feed. Unlike the other (camelCase) pump feeds,
///     these are raw snake_case Mongo documents: <see cref="Value"/> is the alarm code (e.g.
///     "raw_occlusion") and <see cref="AlarmSeverity"/> the severity ("hazard"/"warning"/...). The SSV2
///     counterpart to the v3 graph's <c>pumpAlarm</c> series.
/// </summary>
public class GlookoSsv2Alarm
{
    [JsonPropertyName("pump_timestamp")] public string? PumpTimestamp { get; set; }

    [JsonPropertyName("value")] public string? Value { get; set; }

    [JsonPropertyName("alarm_severity")] public string? AlarmSeverity { get; set; }

    [JsonPropertyName("alarm_type")] public string? AlarmType { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("soft_deleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/cgm/carbs_events</c> (standalone app-logged carbs → CarbIntake).</summary>
public class GlookoCarbsEventPage : GlookoSsv2Page
{
    [JsonPropertyName("carbsEvents")] public GlookoSsv2CarbsEvent[]? CarbsEvents { get; set; }
}

/// <summary>
///     A standalone carb entry logged in the Glooko/CGM app, not attached to a bolus. <see cref="CgmCarbs"/>
///     is the carb amount in grams. The SSV2 counterpart to the v3 graph's <c>carbAll</c> series.
/// </summary>
public class GlookoSsv2CarbsEvent
{
    [JsonPropertyName("displayTime")] public string? DisplayTime { get; set; }

    [JsonPropertyName("eventTime")] public string? EventTime { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    // Wire field is "carbs" (camelCase feed), grams. (Property kept as CgmCarbs for back-compat.)
    [JsonPropertyName("carbs")] public double CgmCarbs { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/cgm/insulin_events</c> (app-logged MDI insulin doses → Bolus/BasalInjection).</summary>
public class GlookoInsulinEventPage : GlookoSsv2Page
{
    [JsonPropertyName("insulinEvents")] public GlookoSsv2InsulinEvent[]? InsulinEvents { get; set; }
}

/// <summary>
///     An app-logged insulin dose from the SSV2 <c>cgm/insulin_events</c> feed — for CGM-only/MDI users
///     who log doses in the app rather than via a pump. Raw snake_case Mongo document. <see cref="Insulin"/>
///     is units; <see cref="InsulinType"/> ("fast_acting" → rapid Bolus, "long_acting"/"intermediate" →
///     long-acting BasalInjection) selects the target. Uses <see cref="DisplayTime"/> (falling back to
///     <see cref="EventTime"/>).
/// </summary>
public class GlookoSsv2InsulinEvent
{
    [JsonPropertyName("insulin")] public double Insulin { get; set; }

    [JsonPropertyName("insulin_type")] public string? InsulinType { get; set; }

    [JsonPropertyName("display_time")] public string? DisplayTime { get; set; }

    [JsonPropertyName("event_time")] public string? EventTime { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("soft_deleted")] public bool SoftDeleted { get; set; }

    [JsonPropertyName("insulin_pen_guid")] public string? InsulinPenGuid { get; set; }
}

/// <summary>A page of <c>/api/v2/notes</c> (app-logged free-text notes → Note).</summary>
public class GlookoNotePage : GlookoSsv2Page
{
    [JsonPropertyName("notes")] public GlookoSsv2Note[]? Notes { get; set; }
}

/// <summary>
///     A free-text note logged in the Glooko app, mapped to <see cref="Nocturne.Core.Models.V4.Note"/>.
///     camelCase. <see cref="Value"/> is the note text, <see cref="Timestamp"/> the time.
/// </summary>
public class GlookoSsv2Note
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("value")] public string? Value { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }

    [JsonPropertyName("manuallyEnteredText")] public bool ManuallyEnteredText { get; set; }
}

/// <summary>A page of <c>/api/v2/exercises</c> (app-logged exercises → Activity).</summary>
public class GlookoExercisePage : GlookoSsv2Page
{
    [JsonPropertyName("exercises")] public GlookoSsv2Exercise[]? Exercises { get; set; }
}

/// <summary>
///     An app-logged exercise from the SSV2 <c>exercises</c> feed → <see cref="Nocturne.Core.Models.Activity"/>.
///     camelCase. <see cref="Duration"/> is in <b>seconds</b> (normalized to minutes on mapping);
///     <see cref="Intensity"/> is numeric (0–100); <see cref="Name"/> is the activity name.
/// </summary>
public class GlookoSsv2Exercise
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("intensity")] public double? Intensity { get; set; }

    /// <summary>Duration in <b>seconds</b> (e.g. 3600 = 1h).</summary>
    [JsonPropertyName("duration")] public double Duration { get; set; }

    [JsonPropertyName("caloriesBurned")] public double? CaloriesBurned { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/cgm/exercise_events</c> (a second app-logged exercise source → Activity).</summary>
public class GlookoExerciseEventPage : GlookoSsv2Page
{
    [JsonPropertyName("exerciseEvents")] public GlookoSsv2ExerciseEvent[]? ExerciseEvents { get; set; }
}

/// <summary>
///     A second app-logged exercise source from the SSV2 <c>cgm/exercise_events</c> feed →
///     <see cref="Nocturne.Core.Models.Activity"/>. Raw snake_case Mongo document. <see cref="Duration"/>
///     is in <b>minutes</b> (e.g. 30) — unlike <see cref="GlookoSsv2Exercise"/> which is seconds; both
///     normalize to minutes on mapping. <see cref="Intensity"/> is a string ("light"/"moderate"/"vigorous").
///     Uses <see cref="DisplayTime"/> (falling back to <see cref="EventTime"/>).
/// </summary>
public class GlookoSsv2ExerciseEvent
{
    [JsonPropertyName("display_time")] public string? DisplayTime { get; set; }

    [JsonPropertyName("event_time")] public string? EventTime { get; set; }

    /// <summary>Duration in <b>minutes</b> (e.g. 30).</summary>
    [JsonPropertyName("duration")] public double Duration { get; set; }

    [JsonPropertyName("intensity")] public string? Intensity { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("soft_deleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/extended_boluses</c> (square/dual-wave boluses).</summary>
public class GlookoExtendedBolusPage : GlookoSsv2Page
{
    [JsonPropertyName("extendedBoluses")] public GlookoExtendedBolus[]? ExtendedBoluses { get; set; }
}

/// <summary>
///     An extended (square) or dual-wave bolus from the SSV2 <c>pumps/extended_boluses</c> feed. Shares
///     the GKBolus/GKPumpObject envelope with <see cref="GlookoBolus"/> plus the extended-delivery fields:
///     <see cref="InitialDelivery"/> (immediate units) + <see cref="ExtendedDelivery"/> (units over
///     <see cref="ExtendedBolusDuration"/>). Net-new — the v3 graph has no extended-bolus series.
/// </summary>
public class GlookoExtendedBolus
{
    [JsonPropertyName("pumpTimestamp")] public string PumpTimestamp { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("insulinDelivered")] public double InsulinDelivered { get; set; }

    [JsonPropertyName("initialDelivery")] public double? InitialDelivery { get; set; }

    [JsonPropertyName("extendedDelivery")] public double? ExtendedDelivery { get; set; }

    /// <summary>
    ///     Duration of the extended portion. Assumed minutes (matching <c>Bolus.Duration</c>) — unverified,
    ///     as the feed is empty on the available test account; confirm against a pump that delivers
    ///     extended boluses (the alternative Glooko convention is seconds).
    /// </summary>
    [JsonPropertyName("extendedBolusDuration")] public double ExtendedBolusDuration { get; set; }

    [JsonPropertyName("carbsInput")] public double CarbsInput { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/pumps/settings</c> (pump basal/bolus program snapshots → Profiles).</summary>
public class GlookoSsv2PumpSettingsPage : GlookoSsv2Page
{
    [JsonPropertyName("settings")] public GlookoSsv2PumpSettings[]? Settings { get; set; }
}

/// <summary>
///     A pump-settings snapshot from the SSV2 <c>pumps/settings</c> feed — raw snake_case Mongo documents
///     (like <see cref="GlookoSsv2Alarm"/>), carrying the basal/bolus programs the pump knows about. The
///     SSV2-native source for Nocturne Profiles, replacing the v3 <c>devices_and_settings</c> call.
///     <see cref="ActiveInsulinTime"/> is DIA in seconds; segment times are seconds-of-day; ISF/target
///     glucose values are mg/dL × 100 (see the individual segment models).
/// </summary>
public class GlookoSsv2PumpSettings
{
    [JsonPropertyName("pump_timestamp")] public string? PumpTimestamp { get; set; }

    [JsonPropertyName("pump_guid")] public string? PumpGuid { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("soft_deleted")] public bool SoftDeleted { get; set; }

    /// <summary>DIA in seconds (e.g. 10800 = 3h). Divide by 3600 for the Nocturne <c>dia</c> (hours).</summary>
    [JsonPropertyName("active_insulin_time")] public double ActiveInsulinTime { get; set; }

    [JsonPropertyName("basal_settings")] public GlookoSsv2BasalSettings[]? BasalSettings { get; set; }

    [JsonPropertyName("bolus_settings")] public GlookoSsv2BolusSettings[]? BolusSettings { get; set; }
}

/// <summary>A single basal program from <c>pumps/settings</c>. <see cref="IsCurrent"/> flags the active one.</summary>
public class GlookoSsv2BasalSettings
{
    [JsonPropertyName("is_current")] public bool IsCurrent { get; set; }

    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }

    [JsonPropertyName("profile_name")] public string? ProfileName { get; set; }

    [JsonPropertyName("segments")] public GlookoSsv2BasalSegment[]? Segments { get; set; }
}

/// <summary>A single bolus program from <c>pumps/settings</c> (ISF / ICR / target-BG segment sets).</summary>
public class GlookoSsv2BolusSettings
{
    [JsonPropertyName("current")] public bool Current { get; set; }

    [JsonPropertyName("profile_id")] public string? ProfileId { get; set; }

    [JsonPropertyName("profile_name")] public string? ProfileName { get; set; }

    [JsonPropertyName("insulin_to_carb_ratio_segments")]
    public GlookoSsv2CarbRatioSegment[]? InsulinToCarbRatioSegments { get; set; }

    [JsonPropertyName("isf_segments")] public GlookoSsv2IsfSegment[]? IsfSegments { get; set; }

    [JsonPropertyName("target_bg_segments")] public GlookoSsv2TargetBgSegment[]? TargetBgSegments { get; set; }
}

/// <summary>
///     Shared segment time window. <see cref="Start"/>/<see cref="End"/> are seconds-of-day (0..86399);
///     <see cref="SegmentId"/> orders the segments within a program.
/// </summary>
public abstract class GlookoSsv2Segment
{
    [JsonPropertyName("start")] public int Start { get; set; }

    [JsonPropertyName("end")] public int End { get; set; }

    [JsonPropertyName("segment_id")] public string? SegmentId { get; set; }
}

/// <summary>A basal-rate segment. <see cref="Rate"/> is U/hr.</summary>
public class GlookoSsv2BasalSegment : GlookoSsv2Segment
{
    [JsonPropertyName("rate")] public double Rate { get; set; }
}

/// <summary>A carb-ratio segment. <see cref="InsulinToCarbsRatio"/> is g/U.</summary>
public class GlookoSsv2CarbRatioSegment : GlookoSsv2Segment
{
    [JsonPropertyName("insulin_to_carbs_ratio")] public double InsulinToCarbsRatio { get; set; }
}

/// <summary>An insulin-sensitivity segment. <see cref="InsulinSensitivityFactor"/> is mg/dL per U × 100.</summary>
public class GlookoSsv2IsfSegment : GlookoSsv2Segment
{
    [JsonPropertyName("insulin_sensitivity_factor")] public double InsulinSensitivityFactor { get; set; }
}

/// <summary>
///     A target-BG segment. <see cref="TargetBg"/>/<see cref="TargetBgLow"/>/<see cref="TargetBgHigh"/> are
///     mg/dL × 100; low/high may be null, in which case the single <see cref="TargetBg"/> is used for both.
/// </summary>
public class GlookoSsv2TargetBgSegment : GlookoSsv2Segment
{
    [JsonPropertyName("target_bg")] public double? TargetBg { get; set; }

    [JsonPropertyName("target_bg_low")] public double? TargetBgLow { get; set; }

    [JsonPropertyName("target_bg_high")] public double? TargetBgHigh { get; set; }
}
/// <summary>A page of <c>/api/v2/pumps</c> (the patient's pump hardware inventory → PatientDevice).</summary>
public class GlookoPumpDevicePage : GlookoSsv2Page
{
    [JsonPropertyName("pumps")] public GlookoSsv2Device[]? Pumps { get; set; }
}

/// <summary>A page of <c>/api/v2/cgm_devices</c> (the patient's CGM hardware inventory → PatientDevice).</summary>
public class GlookoCgmDevicePage : GlookoSsv2Page
{
    [JsonPropertyName("cgmDevices")] public GlookoSsv2Device[]? CgmDevices { get; set; }
}

/// <summary>
///     A single piece of patient hardware from the SSV2 <c>pumps</c> or <c>cgm_devices</c> feed. The two
///     feeds share an identical record shape — only the human-readable model lives under a feed-specific
///     <see cref="GlookoSsv2DeviceProperties"/> key (<c>pumpModel</c> vs <c>cgmModel</c>) — so one model
///     covers both; the device category is decided by which feed the record came from, not by its fields.
/// </summary>
public class GlookoSsv2Device
{
    [JsonPropertyName("brand")] public string? Brand { get; set; }

    [JsonPropertyName("model")] public string? Model { get; set; }

    [JsonPropertyName("serialNumber")] public string? SerialNumber { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("properties")] public GlookoSsv2DeviceProperties? Properties { get; set; }

    /// <summary>Glooko's reference device id (e.g. <c>CAMDIAB_CAMAPS_FX</c>); a coarse catalog hint.</summary>
    [JsonPropertyName("referenceDeviceId")] public string? ReferenceDeviceId { get; set; }

    /// <summary>Most recent time this device uploaded data (fake-UTC, like every Glooko timestamp).</summary>
    [JsonPropertyName("lastSyncTimestamp")] public string? LastSyncTimestamp { get; set; }

    /// <summary>True while this device is the actively-uploading one of its kind for the account.</summary>
    [JsonPropertyName("activelyUploaded")] public bool ActivelyUploaded { get; set; }

    [JsonPropertyName("hidden")] public bool Hidden { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>
///     The feed-specific <c>properties</c> sub-object: the pump feed carries <see cref="PumpModel"/>, the
///     CGM feed <see cref="CgmModel"/>. Both are the precise human-readable model name (e.g.
///     "mylife YpsoPump", "Dexcom G6"), preferred over the record-level <c>model</c> which is the brand's
///     product line (e.g. "CamAPS FX").
/// </summary>
public class GlookoSsv2DeviceProperties
{
    [JsonPropertyName("pumpModel")] public string? PumpModel { get; set; }

    [JsonPropertyName("cgmModel")] public string? CgmModel { get; set; }
}

// ── Health / biometric SSV2 feeds → BodyWeight / StepCount / HeartRate ──────────

/// <summary>A page of <c>/api/v2/weights</c> (manual + HealthKit weight entries → BodyWeight).</summary>
public class GlookoWeightPage : GlookoSsv2Page
{
    [JsonPropertyName("weights")] public GlookoSsv2Weight[]? Weights { get; set; }
}

/// <summary>
///     A weight entry from the SSV2 <c>weights</c> feed (manual / HealthKit). <see cref="Value"/> is in
///     <b>grams</b> (e.g. 86700 = 86.7 kg) regardless of <see cref="WeightUnit"/>, which only names the
///     account's display unit. The third-party counterpart is <see cref="GlookoSsv2ValidicWeight"/>, which
///     carries kilograms directly. → <see cref="Nocturne.Core.Models.BodyWeight"/>.
/// </summary>
public class GlookoSsv2Weight
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    /// <summary>Weight in <b>grams</b> (integer-ish encoding, e.g. 86700 = 86.7 kg).</summary>
    [JsonPropertyName("value")] public double Value { get; set; }

    /// <summary>The account's display unit (e.g. "kg"); informational only — <see cref="Value"/> is always grams.</summary>
    [JsonPropertyName("weightUnit")] public string? WeightUnit { get; set; }

    [JsonPropertyName("manual")] public bool Manual { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }

    [JsonPropertyName("externalId")] public string? ExternalId { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/validic/weights</c> (third-party / Validic weight entries → BodyWeight).</summary>
public class GlookoValidicWeightPage : GlookoSsv2Page
{
    [JsonPropertyName("weights")] public GlookoSsv2ValidicWeight[]? Weights { get; set; }
}

/// <summary>
///     A weight entry from the SSV2 <c>validic/weights</c> feed (third-party integrations: Fitbit, etc.).
///     Unlike <see cref="GlookoSsv2Weight"/>, <see cref="Weight"/> is already in <b>kilograms</b> (e.g. 68),
///     and <see cref="Bmi"/>/<see cref="Height"/> may be present. → <see cref="Nocturne.Core.Models.BodyWeight"/>.
/// </summary>
public class GlookoSsv2ValidicWeight
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("utcOffset")] public string? UtcOffset { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>Weight in <b>kilograms</b> (e.g. 68).</summary>
    [JsonPropertyName("weight")] public double? Weight { get; set; }

    [JsonPropertyName("height")] public double? Height { get; set; }

    [JsonPropertyName("bmi")] public double? Bmi { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/validic/routines</c> (daily activity summary → StepCount).</summary>
public class GlookoRoutinePage : GlookoSsv2Page
{
    [JsonPropertyName("routines")] public GlookoSsv2Routine[]? Routines { get; set; }
}

/// <summary>
///     A daily activity-summary record from the SSV2 <c>validic/routines</c> feed. <see cref="Steps"/> is the
///     day's total step count (a fractional double, e.g. 6717.716, rounded on mapping). The daily-steps source
///     for Nocturne <see cref="Nocturne.Core.Models.StepCount"/> (per-workout steps also appear in
///     <c>validic/fitnesses</c> but are not ingested here to avoid double-counting the daily total).
/// </summary>
public class GlookoSsv2Routine
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("utcOffset")] public string? UtcOffset { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>The day's total step count (fractional double; rounded to an int on mapping).</summary>
    [JsonPropertyName("steps")] public double? Steps { get; set; }

    [JsonPropertyName("distance")] public double? Distance { get; set; }

    [JsonPropertyName("floors")] public double? Floors { get; set; }

    [JsonPropertyName("calories")] public double? Calories { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}

/// <summary>A page of <c>/api/v2/validic/biometric_measurements</c> (biometric panel → HeartRate).</summary>
public class GlookoBiometricMeasurementPage : GlookoSsv2Page
{
    [JsonPropertyName("biometricMeasurements")] public GlookoSsv2BiometricMeasurement[]? BiometricMeasurements { get; set; }
}

/// <summary>
///     A biometric-panel record from the SSV2 <c>validic/biometric_measurements</c> feed — third-party
///     (Validic) lab/vitals data: cholesterol, blood pressure, SpO2, etc. The only heart-rate field Glooko
///     exposes anywhere is <see cref="RestingHeartrate"/> (a resting BPM, present only when the integration
///     supplies it); Glooko has no continuous/time-series HR stream. Records without it are skipped. →
///     <see cref="Nocturne.Core.Models.HeartRate"/>.
/// </summary>
public class GlookoSsv2BiometricMeasurement
{
    [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }

    [JsonPropertyName("utcOffset")] public string? UtcOffset { get; set; }

    [JsonPropertyName("source")] public string? Source { get; set; }

    /// <summary>Resting heart rate in BPM, if the third-party integration reports it; otherwise null.</summary>
    [JsonPropertyName("restingHeartrate")] public double? RestingHeartrate { get; set; }

    [JsonPropertyName("guid")] public string? Guid { get; set; }

    [JsonPropertyName("softDeleted")] public bool SoftDeleted { get; set; }
}
