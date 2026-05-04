using System.Text.Json.Serialization;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Alerts.Conditions;

namespace Nocturne.Core.Models;

/// <summary>
/// Snapshot of current sensor state provided to condition evaluators.
/// All glucose values are in mg/dL; rate is mg/dL per minute.
/// </summary>
public record SensorContext
{
    /// <summary>
    /// Most recent glucose value in mg/dL, or null if no reading is available.
    /// </summary>
    [ReplayFact("latest_glucose", decimals: 0)]
    public required decimal? LatestValue { get; init; }

    /// <summary>
    /// Timestamp of the most recent glucose reading.
    /// </summary>
    public required DateTime? LatestTimestamp { get; init; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute. Positive = rising, negative = falling.
    /// </summary>
    [ReplayFact("trend_rate", decimals: 2)]
    public required decimal? TrendRate { get; init; }

    /// <summary>
    /// Timestamp of the last reading received from the CGM, used for signal loss detection.
    /// </summary>
    [ReplayFact("staleness_minutes", decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public required DateTime? LastReadingAt { get; init; }

    /// <summary>
    /// Coarse trend bucket derived from <see cref="TrendRate"/>. Used by the trend condition.
    /// </summary>
    public TrendBucket? TrendBucket { get; init; }

    /// <summary>
    /// Insulin on board in units, when available from the loop/pump integration.
    /// </summary>
    [ReplayFact("iob", decimals: 2)]
    public decimal? IobUnits { get; init; }

    /// <summary>
    /// Carbohydrates on board in grams, when available from the loop integration.
    /// </summary>
    [ReplayFact("cob", decimals: 1)]
    public decimal? CobGrams { get; init; }

    /// <summary>
    /// Pump reservoir level in units, when available.
    /// </summary>
    [ReplayFact("reservoir", decimals: 1)]
    public decimal? ReservoirUnits { get; init; }

    /// <summary>
    /// Timestamp of the most recent infusion site change. Used by the site-age condition.
    /// </summary>
    [ReplayFact("site_age_hours", decimals: 1, conversion: ReplayFactConversion.HoursSinceNow)]
    public DateTime? LastSiteChangeAt { get; init; }

    /// <summary>
    /// Timestamp of the most recent CGM sensor start. Used by the sensor-age condition.
    /// </summary>
    [ReplayFact("sensor_age_days", decimals: 2, conversion: ReplayFactConversion.DaysSinceNow)]
    public DateTime? LastSensorStartAt { get; init; }

    /// <summary>
    /// Forward-looking glucose predictions used by the predicted condition.
    /// </summary>
    public IReadOnlyList<PredictedGlucosePoint> Predictions { get; init; } = Array.Empty<PredictedGlucosePoint>();

    /// <summary>
    /// Live state of other alerts for this tenant, keyed by alert (rule) id. Used by the alert-state condition.
    /// </summary>
    public IReadOnlyDictionary<Guid, ActiveAlertSnapshot> ActiveAlerts { get; init; } =
        new Dictionary<Guid, ActiveAlertSnapshot>();

    /// <summary>
    /// Identifier of the rule currently being evaluated. Set by the orchestrator;
    /// consumed by stateful evaluators (e.g. sustained) to key persistent timers.
    /// </summary>
    public Guid CurrentRuleId { get; init; }

    /// <summary>
    /// Path identifying the current node within the rule's condition tree (e.g. "composite[0].sustained").
    /// Updated as recursive evaluators descend; consumed by stateful evaluators to key persistent timers.
    /// </summary>
    public string CurrentPath { get; init; } = string.Empty;

    // ----- Looping facts -----

    /// <summary>Timestamp of the latest APS cycle (suggested or enacted), or null when none observed.</summary>
    [ReplayFact("loop_stale_minutes", decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastApsCycleAt { get; init; }

    /// <summary>Timestamp of the latest enacted APS cycle, or null when none observed.</summary>
    [ReplayFact("loop_enaction_stale_minutes", decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastApsEnactedAt { get; init; }

    /// <summary>Latest pump battery level in percent, when available.</summary>
    [ReplayFact("pump_battery_percent", decimals: 0)]
    public decimal? PumpBatteryPercent { get; init; }

    /// <summary>Currently active temp basal projection, or null when no temp is active.</summary>
    public TempBasalSnapshot? ActiveTempBasal { get; init; }

    /// <summary>
    /// Currently-active temp basal rate in U/hr, projected from <see cref="ActiveTempBasal"/>.
    /// Lives as a separate property purely so it can carry a <see cref="ReplayFactAttribute"/>;
    /// evaluators read <see cref="ActiveTempBasal"/> directly.
    /// </summary>
    [ReplayFact("temp_basal_rate", decimals: 2)]
    public decimal? TempBasalRate => ActiveTempBasal?.Rate;

    /// <summary>Latest uploader (phone) battery level in percent, when available.</summary>
    [ReplayFact("uploader_battery_percent", decimals: 0)]
    public decimal? UploaderBatteryPercent { get; init; }

    /// <summary>Currently active override projection, or null when no override is active.</summary>
    public OverrideSnapshot? ActiveOverride { get; init; }

    /// <summary>
    /// Currently active pump-suspension projection, or null when not suspended OR when the
    /// latest pump snapshot is itself stale (preventing latched suspension on offline uploaders).
    /// </summary>
    public PumpSuspensionSnapshot? ActivePumpSuspension { get; init; }

    /// <summary>Latest non-null APS sensitivity ratio (autosens), when available.</summary>
    [ReplayFact("sensitivity_ratio", decimals: 2)]
    public decimal? SensitivityRatio { get; init; }

    /// <summary>
    /// Currently active Do Not Disturb projection, or null when DND is off. Populated from
    /// <c>tenant_alert_settings</c> by the context enricher; collapses both manual and
    /// scheduled DND activation paths into one snapshot, so condition evaluators don't have
    /// to know which path is active.
    /// </summary>
    public DoNotDisturbSnapshot? ActiveDoNotDisturb { get; init; }

    // ----- Cold-start null-suppression flags -----
    // These exist for facts where "no data yet" must be distinguished from "data is just
    // very old" — i.e. where a missing/old timestamp would otherwise satisfy a `>=`
    // operator (LoopStale > 15min on a brand-new tenant). Facts whose projection is
    // naturally null when absent (ActiveOverride, ActiveTempBasal) need no flag.
    // False ⇒ the underlying fact has never been observed for this tenant; the evaluator
    // should return false rather than treat null as "infinity stale" or similar.

    /// <summary>True when the tenant has at least one ApsSnapshot recorded.</summary>
    public bool HasEverApsCycled { get; init; }

    /// <summary>True when the tenant has at least one PumpSnapshot recorded.</summary>
    public bool HasEverPumpSnapshot { get; init; }

    /// <summary>True when the tenant has at least one UploaderSnapshot recorded.</summary>
    public bool HasEverUploaderSnapshot { get; init; }

    /// <summary>True when the tenant has at least one ApsSnapshot with a non-null sensitivity ratio.</summary>
    public bool HasEverApsSensitivity { get; init; }

    // ----- Phase 2 leaves -----

    /// <summary>
    /// Coarse glucose bucket derived from the active TargetRangeEntry boundaries and
    /// <see cref="LatestValue"/>. Null when no glucose reading is available or no target
    /// schedule could be resolved (the GlucoseBucket evaluator returns false in either case).
    /// </summary>
    public GlucoseBucket? GlucoseBucket { get; init; }

    /// <summary>Timestamp of the latest carb-bearing treatment, or null when none observed.</summary>
    [ReplayFact("time_since_last_carb_minutes", decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastCarbAt { get; init; }

    /// <summary>Timestamp of the latest insulin-bearing treatment, or null when none observed.</summary>
    [ReplayFact("time_since_last_bolus_minutes", decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastBolusAt { get; init; }

    /// <summary>
    /// IANA timezone id for the tenant (e.g. <c>"Pacific/Auckland"</c>), resolved from the
    /// active TherapySettings record. Null when the profile has no timezone configured —
    /// the day-of-week evaluator falls back to UTC in that case.
    /// </summary>
    public string? TenantTimeZoneId { get; init; }

    /// <summary>
    /// Currently active pump-mode StateSpan, or null when no pump-mode span is active. Pump
    /// modes are mutually exclusive per StateSpan semantics, so this is a single snapshot.
    /// </summary>
    public PumpStateSnapshot? ActivePumpState { get; init; }

    /// <summary>
    /// Active state-span snapshots keyed by <c>(Category, State)</c>. Populated by the
    /// enricher for every <c>(category, state)</c> pair referenced by the rules being
    /// evaluated. <c>State</c> may be null in the key meaning "any state of this category".
    /// </summary>
    public IReadOnlyDictionary<(StateSpanCategory Category, string? State), StateSpanSnapshot> ActiveStateSpans { get; init; }
        = new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>();
}

/// <summary>
/// A single predicted glucose value at a future offset from "now".
/// </summary>
/// <param name="OffsetMinutes">Minutes ahead of the most recent reading.</param>
/// <param name="Mgdl">Predicted glucose in mg/dL.</param>
public record PredictedGlucosePoint(int OffsetMinutes, decimal Mgdl);

/// <summary>
/// Coarse trend bucket derived from rate of change.
/// </summary>
public enum TrendBucket
{
    /// <summary>Trend cannot be determined (e.g. insufficient data).</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
    /// <summary>Glucose rising rapidly.</summary>
    [JsonStringEnumMemberName("rising_fast")] RisingFast,
    /// <summary>Glucose rising.</summary>
    [JsonStringEnumMemberName("rising")] Rising,
    /// <summary>Glucose flat.</summary>
    [JsonStringEnumMemberName("flat")] Flat,
    /// <summary>Glucose falling.</summary>
    [JsonStringEnumMemberName("falling")] Falling,
    /// <summary>Glucose falling rapidly.</summary>
    [JsonStringEnumMemberName("falling_fast")] FallingFast,
}

/// <summary>
/// Snapshot of another live alert's state, used for cross-alert evaluation.
/// </summary>
/// <param name="State">One of "firing", "unacknowledged", "acknowledged".</param>
/// <param name="TriggeredAt">When the referenced alert first fired.</param>
/// <param name="AcknowledgedAt">When the referenced alert was acknowledged, or null.</param>
public record ActiveAlertSnapshot(string State, DateTime TriggeredAt, DateTime? AcknowledgedAt);

// ----- Condition parameter records (deserialized from JSONB) -----

/// <summary>
/// Threshold-based alert condition. Triggers when glucose crosses <paramref name="Value"/> in the specified <paramref name="Direction"/>.
/// </summary>
/// <param name="Direction">Comparison direction: "above" or "below".</param>
/// <param name="Value">Glucose threshold in mg/dL.</param>
public record ThresholdCondition(string Direction, decimal Value);

/// <summary>
/// Rate-of-change alert condition. Triggers when glucose change rate exceeds <paramref name="Rate"/> in the specified <paramref name="Direction"/>.
/// </summary>
/// <param name="Direction">Rate direction: "rising" or "falling".</param>
/// <param name="Rate">Rate threshold in mg/dL per minute.</param>
public record RateOfChangeCondition(string Direction, decimal Rate);

/// <summary>
/// Signal loss alert condition. Triggers when no CGM reading is received for <paramref name="TimeoutMinutes"/>.
/// </summary>
/// <param name="TimeoutMinutes">Minutes without a reading before triggering.</param>
public record SignalLossCondition([property: JsonPropertyName("timeout_minutes")] int TimeoutMinutes);

/// <summary>
/// Composite alert condition combining multiple child conditions with a logical operator.
/// </summary>
/// <param name="Operator">Logical operator: "and" or "or".</param>
/// <param name="Conditions">Child condition nodes to evaluate.</param>
public record CompositeCondition(string Operator, List<ConditionNode> Conditions);

/// <summary>
/// Logical-NOT wrapper that inverts its child.
/// </summary>
/// <param name="Child">Child node whose evaluation is negated.</param>
public record NotCondition(ConditionNode Child);

/// <summary>
/// Wrapper that fires once <paramref name="Child"/> has held continuously for <paramref name="Minutes"/>.
/// </summary>
/// <param name="Minutes">Duration in minutes the child must remain true.</param>
/// <param name="Child">Inner condition whose continuous truth is timed.</param>
public record SustainedCondition(int Minutes, ConditionNode Child);

/// <summary>
/// Generalised signal-loss condition. Compares minutes since the last reading against <paramref name="Value"/>.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Minutes since last reading to compare against.</param>
public record StalenessCondition(string Operator, int Value);

/// <summary>
/// Predicted glucose comparison within a forecast horizon.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Glucose value in mg/dL to compare against.</param>
/// <param name="WithinMinutes">Forecast horizon in minutes.</param>
public record PredictedCondition(string Operator, decimal Value, [property: JsonPropertyName("within_minutes")] int WithinMinutes);

/// <summary>
/// Trend condition matching a coarse direction bucket.
/// </summary>
/// <param name="Bucket">One of "rising_fast", "rising", "flat", "falling", "falling_fast".</param>
public record TrendCondition(string Bucket);

/// <summary>
/// Time-of-day condition. True when the current local time falls within [<paramref name="From"/>, <paramref name="To"/>).
/// </summary>
/// <param name="From">Window start as "HH:mm".</param>
/// <param name="To">Window end as "HH:mm".</param>
/// <param name="Timezone">IANA timezone id (e.g. "Europe/London"). Null means UTC.</param>
public record TimeOfDayCondition(string From, string To, string? Timezone);

/// <summary>
/// Insulin-on-board comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">IOB in units to compare against.</param>
public record IobCondition(string Operator, decimal Value);

/// <summary>
/// Carbs-on-board comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">COB in grams to compare against.</param>
public record CobCondition(string Operator, decimal Value);

/// <summary>
/// Pump-reservoir comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Reservoir level in units to compare against.</param>
public record ReservoirCondition(string Operator, decimal Value);

/// <summary>
/// Infusion-site age comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Site age in hours to compare against.</param>
public record SiteAgeCondition(string Operator, decimal Value);

/// <summary>
/// CGM sensor age comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Sensor age in days to compare against.</param>
public record SensorAgeCondition(string Operator, decimal Value);

/// <summary>
/// Cross-alert state condition. True when the referenced alert is in the given state, optionally for at least <paramref name="ForMinutes"/>.
/// </summary>
/// <param name="AlertId">The alert (rule) id to inspect.</param>
/// <param name="State">One of "firing", "unacknowledged", "acknowledged".</param>
/// <param name="ForMinutes">When non-null, the condition is true only if the referenced alert has been in the matching state for at least this many minutes — i.e. now - TriggeredAt &gt;= ForMinutes for "firing"/"unacknowledged", or now - AcknowledgedAt &gt;= ForMinutes for "acknowledged".</param>
public record AlertStateCondition(
    [property: JsonPropertyName("alert_id")] Guid AlertId,
    string State,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>Loop liveness — minutes since the latest APS cycle (suggested or enacted).</summary>
public record LoopStaleCondition(string Operator, int Minutes);

/// <summary>Loop enaction liveness — minutes since the latest enacted APS cycle.
/// Open-loop users should not enable this; it would be permanently true.</summary>
public record LoopEnactionStaleCondition(string Operator, int Minutes);

/// <summary>Pump suspension state. Optional ForMinutes measures from the StateSpan start.</summary>
public record PumpSuspendedCondition(
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>Pump battery comparison (percent).</summary>
public record PumpBatteryCondition(string Operator, decimal Value);

/// <summary>Active temp basal comparison. Metric selects rate (U/hr) or percent of scheduled.
/// Returns false when no temp basal is active — the condition concerns active temps only.</summary>
public record TempBasalCondition(TempBasalMetric Metric, string Operator, decimal Value);

/// <summary>Uploader (phone) battery comparison (percent).</summary>
public record UploaderBatteryCondition(string Operator, decimal Value);

/// <summary>Active override state. Optional ForMinutes measures from the StateSpan start.</summary>
public record OverrideActiveCondition(
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>OpenAPS sensitivity ratio (autosens) comparison. AAPS/Trio only;
/// silently false on Loop iOS via null-suppression.</summary>
public record SensitivityRatioCondition(string Operator, decimal Value);

/// <summary>Tenant Do Not Disturb state. Active when DND is on by manual toggle
/// (with optional auto-expire) or by scheduled window. Optional ForMinutes
/// measures from <see cref="DoNotDisturbSnapshot.StartedAt"/>.</summary>
public record DoNotDisturbCondition(
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>Comparison operator for time-since-last-event leaves. Single-character forms
/// match the existing <see cref="ComparisonOps"/> wire format used by the JSON payload.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertComparisonOperator>))]
public enum AlertComparisonOperator
{
    /// <summary>Strictly greater than.</summary>
    [JsonStringEnumMemberName(">")] Gt,
    /// <summary>Greater than or equal.</summary>
    [JsonStringEnumMemberName(">=")] Gte,
    /// <summary>Strictly less than.</summary>
    [JsonStringEnumMemberName("<")] Lt,
    /// <summary>Less than or equal.</summary>
    [JsonStringEnumMemberName("<=")] Lte,
    /// <summary>Equal.</summary>
    [JsonStringEnumMemberName("==")] Eq,
}

/// <summary>Time-since-last-carb comparison, in minutes. A tenant with no carb record observed
/// is treated as <c>+∞</c> minutes elapsed — predicates like "no carbs in 30 min" fire on cold
/// start (matching the audit-trail expectation that absence of evidence is informative).</summary>
public record TimeSinceLastCarbCondition(AlertComparisonOperator Operator, int Minutes);

/// <summary>Time-since-last-bolus comparison, in minutes. Same cold-start semantics as
/// <see cref="TimeSinceLastCarbCondition"/>.</summary>
public record TimeSinceLastBolusCondition(AlertComparisonOperator Operator, int Minutes);

/// <summary>Day-of-week condition. True when the current local day (in the tenant's timezone)
/// falls into <see cref="Days"/>.</summary>
public record DayOfWeekCondition(List<DayOfWeek> Days);

/// <summary>Pump-mode state condition. <see cref="PumpStateCondition.IsActive"/> selects which
/// side of the state is asserted; <see cref="PumpStateCondition.ForMinutes"/> only applies on
/// the IsActive=true side (no anchor exists for "not in mode X for N minutes").</summary>
public record PumpStateCondition(
    PumpModeState Mode,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>Generic state-span-active condition. <see cref="State"/> may be null meaning
/// "any state of this category". The <see cref="StateSpanCategory.PumpMode"/> category is
/// rejected at validation time — use <see cref="PumpStateCondition"/> for pump-mode rules.</summary>
public record StateSpanActiveCondition(
    StateSpanCategory Category,
    string? State,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>Selects which TempBasal field a TempBasalCondition compares.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TempBasalMetric>))]
public enum TempBasalMetric
{
    /// <summary>Compare absolute rate in U/hr.</summary>
    [JsonStringEnumMemberName("rate")] Rate,
    /// <summary>Compare percent of scheduled basal (100 = at schedule).</summary>
    [JsonStringEnumMemberName("percent_of_scheduled")] PercentOfScheduled,
}

/// <summary>
/// A polymorphic condition node in the alert rule condition tree.
/// <paramref name="Type"/> is the discriminator: one of
/// <c>threshold | rate_of_change | signal_loss | composite | not | sustained | staleness | predicted | trend | time_of_day | iob | cob | reservoir | site_age | sensor_age | alert_state | loop_stale | loop_enaction_stale | pump_suspended | pump_battery | temp_basal | uploader_battery | override_active | sensitivity_ratio | do_not_disturb</c>.
/// Exactly one of the optional payload parameters is populated based on <paramref name="Type"/>.
/// </summary>
public record ConditionNode(
    string Type,
    ThresholdCondition? Threshold = null,
    [property: JsonPropertyName("rate_of_change")] RateOfChangeCondition? RateOfChange = null,
    [property: JsonPropertyName("signal_loss")] SignalLossCondition? SignalLoss = null,
    CompositeCondition? Composite = null,
    NotCondition? Not = null,
    SustainedCondition? Sustained = null,
    StalenessCondition? Staleness = null,
    PredictedCondition? Predicted = null,
    TrendCondition? Trend = null,
    [property: JsonPropertyName("time_of_day")] TimeOfDayCondition? TimeOfDay = null,
    IobCondition? Iob = null,
    CobCondition? Cob = null,
    ReservoirCondition? Reservoir = null,
    [property: JsonPropertyName("site_age")] SiteAgeCondition? SiteAge = null,
    [property: JsonPropertyName("sensor_age")] SensorAgeCondition? SensorAge = null,
    [property: JsonPropertyName("alert_state")] AlertStateCondition? AlertState = null,
    [property: JsonPropertyName("loop_stale")] LoopStaleCondition? LoopStale = null,
    [property: JsonPropertyName("loop_enaction_stale")] LoopEnactionStaleCondition? LoopEnactionStale = null,
    [property: JsonPropertyName("pump_suspended")] PumpSuspendedCondition? PumpSuspended = null,
    [property: JsonPropertyName("pump_battery")] PumpBatteryCondition? PumpBattery = null,
    [property: JsonPropertyName("temp_basal")] TempBasalCondition? TempBasal = null,
    [property: JsonPropertyName("uploader_battery")] UploaderBatteryCondition? UploaderBattery = null,
    [property: JsonPropertyName("override_active")] OverrideActiveCondition? OverrideActive = null,
    [property: JsonPropertyName("sensitivity_ratio")] SensitivityRatioCondition? SensitivityRatio = null,
    [property: JsonPropertyName("do_not_disturb")] DoNotDisturbCondition? DoNotDisturb = null,
    [property: JsonPropertyName("glucose_bucket")] GlucoseBucketCondition? GlucoseBucket = null,
    [property: JsonPropertyName("time_since_last_carb")] TimeSinceLastCarbCondition? TimeSinceLastCarb = null,
    [property: JsonPropertyName("time_since_last_bolus")] TimeSinceLastBolusCondition? TimeSinceLastBolus = null,
    [property: JsonPropertyName("day_of_week")] DayOfWeekCondition? DayOfWeek = null,
    [property: JsonPropertyName("pump_state")] PumpStateCondition? PumpState = null,
    [property: JsonPropertyName("state_span_active")] StateSpanActiveCondition? StateSpanActive = null
);

/// <summary>
/// State machine states for excursion tracking within an <see cref="AlertTrackerState"/>.
/// </summary>
public enum TrackerState { Idle, Confirming, Active, Hysteresis }

// ----- Domain models for alert tracker persistence -----

/// <summary>
/// Per-rule state machine tracker. Maps 1:1 with an <see cref="AlertRule"/>.
/// States: idle -> confirming -> active -> hysteresis -> idle.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertExcursion"/>
public class AlertTrackerState
{
    /// <summary>
    /// The <see cref="AlertRule"/> this tracker monitors.
    /// </summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>
    /// Current state machine state. One of: "idle", "confirming", "active", "hysteresis".
    /// </summary>
    /// <seealso cref="TrackerState"/>
    public string State { get; set; } = "idle";

    /// <summary>
    /// Number of consecutive readings that have confirmed the condition during the "confirming" state.
    /// </summary>
    public int ConfirmationCount { get; set; }

    /// <summary>
    /// The currently active <see cref="AlertExcursion"/> ID, set when state transitions to "active".
    /// </summary>
    public Guid? ActiveExcursionId { get; set; }

    /// <summary>
    /// Timestamp of the last state transition.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A composable alert rule with condition tree, hysteresis, and confirmation settings.
/// </summary>
/// <seealso cref="AlertTrackerState"/>
/// <seealso cref="AlertExcursion"/>
/// <seealso cref="Alerts.AlertConditionType"/>
/// <seealso cref="Alerts.AlertRuleSeverity"/>
public class AlertRule
{
    /// <summary>Unique identifier for the rule.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of what this rule monitors.</summary>
    public string? Description { get; set; }

    /// <summary>The type of condition this rule evaluates.</summary>
    /// <seealso cref="Alerts.AlertConditionType"/>
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;

    /// <summary>JSON-serialized condition parameters (deserialized into <see cref="ConditionNode"/>).</summary>
    public string ConditionParams { get; set; } = "{}";

    /// <summary>Minutes to wait after condition clears before transitioning back to idle.</summary>
    public int HysteresisMinutes { get; set; }

    /// <summary>Number of consecutive readings required to confirm the condition before triggering.</summary>
    public int ConfirmationReadings { get; set; } = 1;

    /// <summary>Severity level of alerts generated by this rule.</summary>
    /// <seealso cref="Alerts.AlertRuleSeverity"/>
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    /// <summary>JSON-serialized client configuration (sound, vibration, display preferences).</summary>
    public string ClientConfiguration { get; set; } = "{}";

    /// <summary>Whether this rule is actively being evaluated.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Display order among rules.</summary>
    public int SortOrder { get; set; }

    /// <summary>Whether this rule auto-resolves (clears) when its condition no longer holds.</summary>
    public bool AutoResolveEnabled { get; set; }

    /// <summary>JSON-serialized auto-resolve parameters (e.g. delay, mode), or null if unused.</summary>
    public string? AutoResolveParams { get; set; }

    /// <summary>When this rule was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this rule was last modified.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single continuous excursion (out-of-range episode) for an <see cref="AlertRule"/>.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertTrackerState"/>
public class AlertExcursion
{
    /// <summary>Unique identifier for this excursion.</summary>
    public Guid Id { get; set; }

    /// <summary>The <see cref="AlertRule"/> that triggered this excursion.</summary>
    public Guid AlertRuleId { get; set; }

    /// <summary>When the excursion began.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When the excursion ended. Null if still active.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>When a user acknowledged this excursion.</summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Identifier of the user who acknowledged this excursion.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>When the hysteresis cooldown period began after the condition cleared.</summary>
    public DateTime? HysteresisStartedAt { get; set; }
}

/// <summary>
/// Structured alert payload delivered to notification providers (push, email, etc.).
/// Contains all data needed to render an alert message; no pre-rendered text.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertExcursion"/>
public record AlertPayload
{
    /// <summary>The condition type that triggered this alert.</summary>
    public required AlertConditionType AlertType { get; init; }

    /// <summary>Human-readable name of the <see cref="AlertRule"/> that fired.</summary>
    public required string RuleName { get; init; }

    /// <summary>Current glucose value in mg/dL at the time of the alert.</summary>
    public required decimal? GlucoseValue { get; init; }

    /// <summary>Glucose trend direction string (e.g., "Flat", "SingleUp").</summary>
    public required string? Trend { get; init; }

    /// <summary>Rate of glucose change in mg/dL per minute.</summary>
    public required decimal? TrendRate { get; init; }

    /// <summary>Timestamp of the glucose reading that triggered the alert.</summary>
    public required DateTime ReadingTimestamp { get; init; }

    /// <summary>The <see cref="AlertExcursion"/> that this alert belongs to.</summary>
    public required Guid ExcursionId { get; init; }

    /// <summary>The alert instance ID for delivery tracking.</summary>
    public required Guid InstanceId { get; init; }

    /// <summary>Tenant (user) this alert belongs to.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Display name of the subject (person being monitored).</summary>
    public required string SubjectName { get; init; }

    /// <summary>Total number of active excursions across all rules for this tenant.</summary>
    public required int ActiveExcursionCount { get; init; }

    /// <summary>
    /// The firing rule's severity. Drives downstream rendering: <see cref="Alerts.AlertRuleSeverity.Critical"/>
    /// rules bypass quiet hours and render with urgent visual treatment in InApp/push channels.
    /// </summary>
    public required Alerts.AlertRuleSeverity Severity { get; init; }
}
