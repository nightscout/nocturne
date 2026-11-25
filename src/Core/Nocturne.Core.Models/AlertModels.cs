using System.Text.Json.Serialization;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Alerts.Conditions;
using Nocturne.Core.Models.Authorization;

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
    [ReplayFact("latest_glucose", Scope.GlucoseRead, decimals: 0)]
    public required decimal? LatestValue { get; init; }

    /// <summary>
    /// Timestamp of the most recent glucose reading.
    /// </summary>
    public required DateTime? LatestTimestamp { get; init; }

    /// <summary>
    /// When the host stored the most recent reading. Its glucose facts' age runs from here
    /// (docs/alerts/engine-semantics.md §3); null runs it from <see cref="LatestTimestamp"/>.
    /// </summary>
    public DateTime? LatestReceivedAt { get; init; }

    /// <summary>
    /// Rate of glucose change in mg/dL per minute. Positive = rising, negative = falling.
    /// </summary>
    [ReplayFact("trend_rate", Scope.GlucoseRead, decimals: 2)]
    public required decimal? TrendRate { get; init; }

    /// <summary>
    /// Timestamp of the last reading received from the CGM, used for signal loss detection.
    /// </summary>
    [ReplayFact("staleness_minutes", Scope.GlucoseRead, decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public required DateTime? LastReadingAt { get; init; }

    /// <summary>
    /// Coarse trend bucket of <see cref="TrendRate"/>, or of the reading's arrow when no rate is
    /// known. Used by the trend condition.
    /// </summary>
    public TrendBucket? TrendBucket { get; init; }

    /// <summary>
    /// Insulin on board in units, when available from the loop/pump integration.
    /// </summary>
    [ReplayFact("iob", Scope.TreatmentsRead, decimals: 2)]
    public decimal? IobUnits { get; init; }

    /// <summary>
    /// Carbohydrates on board in grams, when available from the loop integration.
    /// </summary>
    [ReplayFact("cob", Scope.TreatmentsRead, decimals: 1)]
    public decimal? CobGrams { get; init; }

    /// <summary>
    /// Pump reservoir level in units, when available.
    /// </summary>
    [ReplayFact("reservoir", Scope.DevicesRead, decimals: 1)]
    public decimal? ReservoirUnits { get; init; }

    /// <summary>
    /// True when <see cref="ReservoirUnits"/> is a lower bound rather than an exact reading
    /// (e.g. an Omnipod reports "50+" while the reservoir holds at least 50 units).
    /// Replay emits this fact as 0/1.
    /// </summary>
    [ReplayFact("reservoir_is_lower_bound", Scope.DevicesRead, decimals: 0)]
    public bool ReservoirIsLowerBound { get; init; }

    /// <summary>
    /// Timestamp of the most recent infusion site change. Used by the site-age condition.
    /// </summary>
    [ReplayFact("site_age_hours", Scope.DevicesRead, decimals: 1, conversion: ReplayFactConversion.HoursSinceNow)]
    public DateTime? LastSiteChangeAt { get; init; }

    /// <summary>
    /// Timestamp of the most recent CGM sensor start. Used by the sensor-age condition.
    /// </summary>
    [ReplayFact("sensor_age_days", Scope.DevicesRead, decimals: 2, conversion: ReplayFactConversion.DaysSinceNow)]
    public DateTime? LastSensorStartAt { get; init; }

    /// <summary>
    /// The <see cref="ForecastCurve.Default"/> forecast series: whatever series the prediction
    /// source has always returned as its main curve.
    /// </summary>
    public IReadOnlyList<PredictedGlucosePoint> Predictions { get; init; } = Array.Empty<PredictedGlucosePoint>();

    /// <summary>
    /// The named forecast curves other than <see cref="ForecastCurve.Default"/>, each as the
    /// source returned it; a curve the source has none of is absent.
    /// </summary>
    public IReadOnlyDictionary<ForecastCurve, IReadOnlyList<PredictedGlucosePoint>> PredictionCurves { get; init; }
        = new Dictionary<ForecastCurve, IReadOnlyList<PredictedGlucosePoint>>();

    /// <summary>
    /// The loop's newest determination, or null with none. Read by the <c>eventual_glucose</c> and
    /// <c>carbs_required</c> conditions.
    /// </summary>
    public ApsDetermination? ApsDetermination { get; init; }

    /// <summary>
    /// Live state of other alerts for this tenant, keyed by alert (rule) id. Used by the alert-state condition.
    /// </summary>
    public IReadOnlyDictionary<Guid, ActiveAlertSnapshot> ActiveAlerts { get; init; } =
        new Dictionary<Guid, ActiveAlertSnapshot>();

    // ----- Looping facts -----

    /// <summary>Timestamp of the latest APS cycle (suggested or enacted), or null when none observed.</summary>
    [ReplayFact("loop_stale_minutes", Scope.DevicesRead, decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastApsCycleAt { get; init; }

    /// <summary>Timestamp of the latest enacted APS cycle, or null when none observed.</summary>
    [ReplayFact("loop_enaction_stale_minutes", Scope.DevicesRead, decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastApsEnactedAt { get; init; }

    /// <summary>Latest pump battery level in percent, when available.</summary>
    [ReplayFact("pump_battery_percent", Scope.DevicesRead, decimals: 0)]
    public decimal? PumpBatteryPercent { get; init; }

    /// <summary>Currently active temp basal projection, or null when no temp is active.</summary>
    public TempBasalSnapshot? ActiveTempBasal { get; init; }

    /// <summary>
    /// Currently-active temp basal rate in U/hr, projected from <see cref="ActiveTempBasal"/>.
    /// Lives as a separate property purely so it can carry a <see cref="ReplayFactAttribute"/>;
    /// evaluators read <see cref="ActiveTempBasal"/> directly.
    /// </summary>
    [ReplayFact("temp_basal_rate", Scope.TreatmentsRead, decimals: 2)]
    public decimal? TempBasalRate => ActiveTempBasal?.Rate;

    /// <summary>Latest uploader (phone) battery level in percent, when available.</summary>
    [ReplayFact("uploader_battery_percent", Scope.DevicesRead, decimals: 0)]
    public decimal? UploaderBatteryPercent { get; init; }

    /// <summary>
    /// The CGM transmitter's latest reported battery readings (<see cref="V4.TransmitterStatus"/>), each
    /// null until an uploader reports it.
    /// </summary>
    [ReplayFact("transmitter_voltage_a", Scope.DevicesRead, decimals: 0)]
    public decimal? TransmitterVoltageA { get; init; }

    /// <inheritdoc cref="TransmitterVoltageA"/>
    [ReplayFact("transmitter_voltage_b", Scope.DevicesRead, decimals: 0)]
    public decimal? TransmitterVoltageB { get; init; }

    /// <inheritdoc cref="TransmitterVoltageA"/>
    [ReplayFact("transmitter_resistance", Scope.DevicesRead, decimals: 0)]
    public decimal? TransmitterResistance { get; init; }

    /// <inheritdoc cref="TransmitterVoltageA"/>
    [ReplayFact("transmitter_days", Scope.DevicesRead, decimals: 0)]
    public decimal? TransmitterDays { get; init; }

    /// <summary>Currently active override projection, or null when no override is active.</summary>
    public OverrideSnapshot? ActiveOverride { get; init; }

    /// <summary>
    /// Currently active pump-suspension projection, or null when not suspended OR when the
    /// latest pump snapshot is itself stale (preventing latched suspension on offline uploaders).
    /// </summary>
    public PumpSuspensionSnapshot? ActivePumpSuspension { get; init; }

    /// <summary>Latest non-null APS sensitivity ratio (autosens), when available.</summary>
    [ReplayFact("sensitivity_ratio", Scope.DevicesRead, decimals: 2)]
    public decimal? SensitivityRatio { get; init; }

    /// <summary>
    /// Currently active Do Not Disturb projection, or null when DND is off. Drives the
    /// <c>do_not_disturb</c> condition leaf and the tenant-wide notion of DND. Re-sourced by
    /// the enricher from the active <c>scope=all</c> DND window and scheduled DND (ADR 0004 D5):
    /// <c>lows</c>/<c>highs</c> windows feed <see cref="ActiveDndScopes"/> only and never trip
    /// this. Non-null exactly when <see cref="ActiveDndScopes"/> contains <see cref="DndScope.All"/>.
    /// </summary>
    public DoNotDisturbSnapshot? ActiveDoNotDisturb { get; init; }

    /// <summary>
    /// The set of DND scopes active as of this context's evaluation instant (resolved on read
    /// from the tenant's <c>dnd_windows</c> plus scheduled DND), for scoped Do Not Disturb
    /// suppression (ADR 0004 D5). The shared <c>DndSuppressionGate</c> silences a rule when one
    /// of these scopes covers the rule's <see cref="AlertRuleSnapshot.ScopeClass"/>. Empty when
    /// no DND is active. Assembled live (<c>IsActiveAt(now)</c>) or, in replay, receipt-gated
    /// (<c>WasActiveAt(T)</c>).
    /// </summary>
    public IReadOnlySet<DndScope> ActiveDndScopes { get; init; } = new HashSet<DndScope>();

    // ----- Cold-start null-suppression flags -----
    // These exist for facts where "no data yet" must be distinguished from "data is just
    // very old" — i.e. where a missing/old timestamp would otherwise satisfy a `>=`
    // operator (LoopStale > 15min on a brand-new tenant). Facts whose projection is
    // naturally null when absent (ActiveOverride, ActiveTempBasal) need no flag.
    // False ⇒ the underlying fact has never been observed for this tenant; the engine
    // returns false rather than treat null as "infinity stale" or similar.

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

    /// <summary>
    /// The low end of the profile's target range in effect at the evaluation instant, in mg/dL: the
    /// entry <see cref="GlucoseBucket"/> is placed with. Null when the profile has no target range,
    /// where the bucket falls back to its defaults and this never does
    /// (docs/alerts/engine-semantics.md §4).
    /// </summary>
    public decimal? TargetLow { get; init; }

    /// <summary>The high end of the range <see cref="TargetLow"/> is the low end of.</summary>
    public decimal? TargetHigh { get; init; }

    /// <summary>Timestamp of the latest carb-bearing treatment, or null when none observed.</summary>
    [ReplayFact("time_since_last_carb_minutes", Scope.TreatmentsRead, decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastCarbAt { get; init; }

    /// <summary>Timestamp of the latest insulin-bearing treatment, or null when none observed.</summary>
    [ReplayFact("time_since_last_bolus_minutes", Scope.TreatmentsRead, decimals: 0, conversion: ReplayFactConversion.MinutesSinceNow)]
    public DateTime? LastBolusAt { get; init; }

    /// <summary>
    /// IANA timezone id for the tenant (e.g. <c>"Pacific/Auckland"</c>), resolved from the
    /// active TherapySettings record. Null when the profile has no timezone configured —
    /// the day-of-week evaluator falls back to UTC in that case.
    /// </summary>
    public string? TenantTimeZoneId { get; init; }

    /// <summary>
    /// Every active pump-mode StateSpan. Spans of different modes overlap: a Suspended span runs
    /// on top of an Automatic or Manual one. Populated by the enricher when a rule reads
    /// <c>pump_state</c>; empty when no pump-mode span is active.
    /// </summary>
    public IReadOnlyList<PumpModeSnapshot> ActivePumpModes { get; init; } = [];

    /// <summary>
    /// Active state-span snapshots keyed by <c>(Category, State)</c>. Populated by the
    /// enricher for every <c>(category, state)</c> pair referenced by the rules being
    /// evaluated. <c>State</c> may be null in the key meaning "any state of this category".
    /// </summary>
    public IReadOnlyDictionary<(StateSpanCategory Category, string? State), StateSpanSnapshot> ActiveStateSpans { get; init; }
        = new Dictionary<(StateSpanCategory, string?), StateSpanSnapshot>();

    /// <summary>
    /// True when a sleep session (from the sleep_sessions tables) has
    /// <c>StartTime &lt;= now &lt;= EndTime</c> for the tenant. Populated by the enricher only
    /// when a rule references the <c>sleep_session_active</c> condition; false otherwise.
    /// </summary>
    public bool SleepSessionActive { get; init; }

    /// <summary>
    /// The active tracker instance per tracker definition. Populated by the enricher for every
    /// definition referenced by a <c>tracker_age</c> leaf in the rules being evaluated. An
    /// absent key means no active instance — the leaf evaluates false.
    /// </summary>
    public IReadOnlyDictionary<Guid, ActiveTrackerReference> ActiveTrackers { get; init; }
        = new Dictionary<Guid, ActiveTrackerReference>();

    /// <summary>
    /// The sensor's state, or null when nothing is known of it, which the glucose leaves read as
    /// they would without it. <see cref="Alerts.SensorState.NoValue"/> when the newest reading has
    /// no glucose value and no source reports a state other than ok.
    /// </summary>
    public SensorStateSnapshot? SensorState { get; init; }

    /// <summary>
    /// Where the newest reading came from, which a reported sensor state must match to describe it.
    /// The host reads it; it is not sent to the engine.
    /// </summary>
    public ReadingSource? LatestSource { get; init; }

    /// <summary>
    /// What a live pass saw of the stored pump-mode spans, or null when it read none. The host
    /// reports a clock anomaly from it; it is not sent to the engine.
    /// </summary>
    public SpansRead? PumpModesRead { get; init; }

    /// <summary>
    /// What a live pass saw of the overrides a device status wrote, or null when it read none. Only
    /// those date an override by a device's clock; any other is a plan. The host reports a clock
    /// anomaly from it; it is not sent to the engine.
    /// </summary>
    public SpansRead? OverridesRead { get; init; }
}

/// <summary>The device and data source a reading was written under.</summary>
public sealed record ReadingSource(string? DataSource, string? Device);

/// <summary>What a live pass saw of the stored spans of one context fact.</summary>
/// <param name="At">The instant the pass chose the spans to send at, which the host judges them against.</param>
/// <param name="NewestStart">
/// The latest start among the spans not ended before <paramref name="At"/>, open or closed, sent or not, or null
/// when there is none.
/// </param>
public sealed record SpansRead(DateTime At, DateTime? NewestStart);

/// <summary>A tracker definition's active instance, as the <c>tracker_age</c> leaf reads it.</summary>
/// <param name="ReferenceAt">Start time for a Duration tracker, scheduled time for an Event tracker.</param>
/// <param name="RecordedAt">
/// When the instance was recorded. A threshold that already held then does not fire for a Duration
/// instance (docs/alerts/engine-semantics.md §3). Null holds nothing back.
/// </param>
/// <param name="IsScheduled">An Event tracker's instance, which is never held back.</param>
public sealed record ActiveTrackerReference(DateTime ReferenceAt, DateTime? RecordedAt, bool IsScheduled = false);

/// <summary>
/// A single predicted glucose value at a future offset from "now".
/// </summary>
/// <param name="OffsetMinutes">Minutes ahead of the most recent reading.</param>
/// <param name="Mgdl">Predicted glucose in mg/dL.</param>
public record PredictedGlucosePoint(int OffsetMinutes, decimal Mgdl);

/// <summary>
/// Coarse trend bucket of the rate of change, or of the reading's arrow when no rate is known.
/// </summary>
public enum TrendBucket
{
    /// <summary>Trend cannot be determined (e.g. insufficient data).</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
    /// <summary>Glucose rising rapidly.</summary>
    [JsonStringEnumMemberName("rising_fast")] RisingFast,
    [JsonStringEnumMemberName("rising")] Rising,
    [JsonStringEnumMemberName("flat")] Flat,
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
/// Threshold-based alert condition. Triggers when glucose crosses its limit in the specified <paramref name="Direction"/>.
/// </summary>
/// <param name="Direction">Comparison direction: "above" or "below".</param>
/// <param name="Value">Glucose threshold in mg/dL; exactly one of it and <paramref name="ValueFrom"/>.</param>
/// <param name="ClearMargin">How far past its limit the condition stays true while the rule's alert
/// is open, in the unit of its value; absent or 0 for none (docs/alerts/engine-semantics.md §3).</param>
/// <param name="ValueFrom">The fact whose value, plus its offset, is the limit (docs/alerts/engine-semantics.md
/// §3, a limit from a fact). Never <see cref="AlertLimitFact.LatestGlucose"/>, which would compare the
/// reading with itself.</param>
public record ThresholdCondition(
    string Direction,
    decimal? Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null,
    [property: JsonPropertyName("value_from")] AlertLimitReference? ValueFrom = null);

/// <summary>
/// Rate-of-change alert condition. Triggers when glucose change rate exceeds <paramref name="Rate"/> in the specified <paramref name="Direction"/>.
/// </summary>
/// <param name="Direction">Rate direction: "rising" or "falling".</param>
/// <param name="Rate">Rate threshold in mg/dL per minute.</param>
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>, in mg/dL per minute.</param>
public record RateOfChangeCondition(
    string Direction,
    decimal Rate,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

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
/// Predicted glucose comparison within a forecast horizon, over the series at most one of
/// <paramref name="Curve"/>, <paramref name="MinOf"/> and <paramref name="MaxOf"/> selects; none reads
/// <see cref="ForecastCurve.Default"/> (docs/alerts/engine-semantics.md §3, forecast curves).
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Glucose value in mg/dL to compare against; exactly one of it and <paramref name="ValueFrom"/>.</param>
/// <param name="WithinMinutes">Forecast horizon in minutes.</param>
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>, for every point.</param>
/// <param name="Curve">One named series.</param>
/// <param name="MinOf">The lowest of these series at each offset they all have.</param>
/// <param name="MaxOf">The highest of these series at each offset they all have.</param>
/// <param name="ValueFrom">As <see cref="ThresholdCondition.ValueFrom"/>; any of the facts.</param>
public record PredictedCondition(
    string Operator,
    decimal? Value,
    [property: JsonPropertyName("within_minutes")] int WithinMinutes,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null,
    [property: JsonPropertyName("curve")] ForecastCurve? Curve = null,
    [property: JsonPropertyName("min_of")] List<ForecastCurve>? MinOf = null,
    [property: JsonPropertyName("max_of")] List<ForecastCurve>? MaxOf = null,
    [property: JsonPropertyName("value_from")] AlertLimitReference? ValueFrom = null);

/// <summary>
/// The glucose the loop's newest determination expected at the end of its forecast, in mg/dL,
/// compared with <paramref name="Value"/>. Unknown with no determination, or one older than the
/// rule's glucose freshness bound.
/// </summary>
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>.</param>
/// <param name="ValueFrom">As <see cref="PredictedCondition.ValueFrom"/>.</param>
public record EventualGlucoseCondition(
    string Operator,
    decimal? Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null,
    [property: JsonPropertyName("value_from")] AlertLimitReference? ValueFrom = null);

/// <summary>
/// The carbohydrate the loop's newest determination said it needed, in grams, compared with
/// <paramref name="Value"/>. Unknown as <see cref="EventualGlucoseCondition"/> is, and for a loop
/// that does not report it.
/// </summary>
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>.</param>
public record CarbsRequiredCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

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
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>.</param>
public record IobCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>
/// Carbs-on-board comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">COB in grams to compare against.</param>
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>.</param>
public record CobCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>
/// Pump-reservoir comparison.
/// </summary>
/// <param name="Operator">Comparison operator: "&lt;", "&lt;=", "&gt;", "&gt;=", or "==".</param>
/// <param name="Value">Reservoir level in units to compare against.</param>
/// <param name="ClearMargin">As <see cref="ThresholdCondition.ClearMargin"/>.</param>
public record ReservoirCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

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

/// <summary>Pump battery comparison (percent). <c>ClearMargin</c> as <see cref="ThresholdCondition.ClearMargin"/>.</summary>
public record PumpBatteryCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>Active temp basal comparison. Metric selects rate (U/hr) or percent of scheduled.
/// Returns false when no temp basal is active — the condition concerns active temps only.
/// <c>ClearMargin</c> as <see cref="ThresholdCondition.ClearMargin"/>.</summary>
public record TempBasalCondition(
    TempBasalMetric Metric,
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>Uploader (phone) battery comparison (percent). <c>ClearMargin</c> as
/// <see cref="ThresholdCondition.ClearMargin"/>.</summary>
public record UploaderBatteryCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>CGM transmitter battery comparison. Metric selects the reading; a reading no uploader
/// has reported is false. <c>ClearMargin</c> as <see cref="ThresholdCondition.ClearMargin"/>.</summary>
public record TransmitterBatteryCondition(
    TransmitterBatteryMetric Metric,
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>Active override state. Optional ForMinutes measures from the StateSpan start.</summary>
public record OverrideActiveCondition(
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>OpenAPS sensitivity ratio (autosens) comparison. AAPS/Trio only;
/// silently false on Loop iOS via null-suppression. <c>ClearMargin</c> as
/// <see cref="ThresholdCondition.ClearMargin"/>.</summary>
public record SensitivityRatioCondition(
    string Operator,
    decimal Value,
    [property: JsonPropertyName("clear_margin")] decimal? ClearMargin = null);

/// <summary>Tenant Do Not Disturb state. Active when DND is on by manual toggle
/// (with optional auto-expire) or by scheduled window. Optional ForMinutes
/// measures from <see cref="DoNotDisturbSnapshot.StartedAt"/>.</summary>
public record DoNotDisturbCondition(
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("for_minutes")] int? ForMinutes);

/// <summary>Comparison operator for time-since-last-event leaves. Single-character forms
/// match the operator wire format the other comparison leaves use.</summary>
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
public record DayOfWeekCondition(List<AlertWeekday> Days);

/// <summary>A day a <see cref="DayOfWeekCondition"/> names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertWeekday>))]
public enum AlertWeekday
{
    [JsonStringEnumMemberName("sunday")] Sunday,
    [JsonStringEnumMemberName("monday")] Monday,
    [JsonStringEnumMemberName("tuesday")] Tuesday,
    [JsonStringEnumMemberName("wednesday")] Wednesday,
    [JsonStringEnumMemberName("thursday")] Thursday,
    [JsonStringEnumMemberName("friday")] Friday,
    [JsonStringEnumMemberName("saturday")] Saturday,
}

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

/// <summary>Sleep-session-active condition. True when a sleep session (from the sleep_sessions
/// tables) has <c>StartTime &lt;= now &lt;= EndTime</c> for the tenant. <see cref="IsActive"/>
/// selects which side of the boolean is asserted: <c>true</c> matches while a session is active,
/// <c>false</c> matches when none is.</summary>
public record SleepSessionActiveCondition(
    [property: JsonPropertyName("is_active")] bool IsActive);

/// <summary>Tracker-age condition: minutes since the active tracker instance's reference
/// timestamp (start for Duration trackers, scheduled time for Event trackers) compared
/// against <see cref="Minutes"/>. Elapsed time is negative before a scheduled event, so a
/// negative <see cref="Minutes"/> with <c>&gt;=</c> expresses "within N minutes before the
/// event". No active instance for the definition evaluates false — a tracker that isn't
/// running has no age (deliberately opposite to the time_since_last_* cold-start infinity).</summary>
public record TrackerAgeCondition(
    [property: JsonPropertyName("tracker_definition_id")] Guid TrackerDefinitionId,
    string Operator,
    int Minutes);

/// <summary>Selects which TempBasal field a TempBasalCondition compares.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TempBasalMetric>))]
public enum TempBasalMetric
{
    /// <summary>Compare absolute rate in U/hr.</summary>
    [JsonStringEnumMemberName("rate")] Rate,
    /// <summary>Compare percent of scheduled basal (100 = at schedule).</summary>
    [JsonStringEnumMemberName("percent_of_scheduled")] PercentOfScheduled,
}

/// <summary>Selects which <see cref="V4.TransmitterStatus"/> reading a TransmitterBatteryCondition compares.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TransmitterBatteryMetric>))]
public enum TransmitterBatteryMetric
{
    [JsonStringEnumMemberName("voltage_a")] VoltageA,
    [JsonStringEnumMemberName("voltage_b")] VoltageB,
    [JsonStringEnumMemberName("resistance")] Resistance,
    /// <summary>Days since the transmitter was activated.</summary>
    [JsonStringEnumMemberName("days")] Days,
}

/// <summary>
/// A polymorphic condition node in the alert rule condition tree.
/// <paramref name="Type"/> is the discriminator: one of
/// <c>threshold | rate_of_change | signal_loss | composite | not | sustained | staleness | predicted | trend | time_of_day | iob | cob | reservoir | site_age | sensor_age | alert_state | loop_stale | loop_enaction_stale | pump_suspended | pump_battery | temp_basal | uploader_battery | override_active | sensitivity_ratio | do_not_disturb | transmitter_battery | sensor_state | eventual_glucose | carbs_required</c>.
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
    [property: JsonPropertyName("state_span_active")] StateSpanActiveCondition? StateSpanActive = null,
    [property: JsonPropertyName("sleep_session_active")] SleepSessionActiveCondition? SleepSessionActive = null,
    [property: JsonPropertyName("tracker_age")] TrackerAgeCondition? TrackerAge = null,
    [property: JsonPropertyName("transmitter_battery")] TransmitterBatteryCondition? TransmitterBattery = null,
    [property: JsonPropertyName("sensor_state")] SensorStateCondition? SensorState = null,
    [property: JsonPropertyName("eventual_glucose")] EventualGlucoseCondition? EventualGlucose = null,
    [property: JsonPropertyName("carbs_required")] CarbsRequiredCondition? CarbsRequired = null
);

/// <summary>
/// State machine states for excursion tracking within an <see cref="AlertTrackerState"/>.
/// </summary>
public enum TrackerState { Idle, Active, Hysteresis }

// ----- Domain models for alert tracker persistence -----

/// <summary>
/// Per-rule state machine tracker. Maps 1:1 with an <see cref="AlertRule"/>.
/// States: idle -> active -> hysteresis -> idle.
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
    /// Current state machine state. One of: "idle", "active", "hysteresis".
    /// </summary>
    /// <seealso cref="TrackerState"/>
    public string State { get; set; } = "idle";

    /// <summary>
    /// The currently active <see cref="AlertExcursion"/> ID, set when state transitions to "active".
    /// </summary>
    public Guid? ActiveExcursionId { get; set; }

    /// <summary>
    /// Timestamp of the last evaluation.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// When the active excursion entered hysteresis; set only in the "hysteresis" state.
    /// Hysteresis expires <see cref="AlertRule.HysteresisMinutes"/> after this instant.
    /// </summary>
    public DateTime? HysteresisStartedAt { get; set; }

    /// <summary>
    /// Idle after an auto-resolve closed an excursion whose condition still held: the rule opens
    /// nothing until an evaluation finds its condition false (docs/alerts/engine-semantics.md §6.3).
    /// </summary>
    public bool AwaitingRearm { get; set; }

    /// <summary>
    /// The next change (docs/alerts/engine-semantics.md §5.3) the rule's last full evaluation
    /// reported, null when it reported none. Read only while <see cref="NextChangeKnown"/>.
    /// </summary>
    public DateTime? NextChangeAt { get; set; }

    /// <summary>
    /// Whether <see cref="NextChangeAt"/> was recorded by a full evaluation of the rule as it now
    /// stands, with nothing but such an evaluation writing the state since.
    /// </summary>
    public bool NextChangeKnown { get; set; }

    /// <summary>
    /// The glucose facts of the context <see cref="NextChangeAt"/> was computed from: it holds
    /// only while a context carries the same ones.
    /// </summary>
    public string? NextChangeGlucose { get; set; }

    /// <summary>
    /// When the rule's current hold began: the first of an unbroken run of evaluations whose
    /// unknown root it holds (docs/alerts/engine-semantics.md §6.4). Null while it is not held.
    /// </summary>
    public DateTime? HeldSince { get; set; }

    /// <summary>
    /// The facts the hold is waiting for, as <see cref="Alerts.HeldFacts.Format"/> writes them; set
    /// only with <see cref="HeldSince"/>.
    /// </summary>
    public string? HeldFacts { get; set; }
}

/// <summary>
/// A composable alert rule with condition tree and hysteresis settings.
/// </summary>
/// <seealso cref="AlertTrackerState"/>
/// <seealso cref="AlertExcursion"/>
/// <seealso cref="Alerts.AlertConditionType"/>
/// <seealso cref="Alerts.AlertRuleSeverity"/>
public class AlertRule
{
    public Guid Id { get; set; }

    /// <summary>Human-readable rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description of what this rule monitors.</summary>
    public string? Description { get; set; }

    /// <seealso cref="Alerts.AlertConditionType"/>
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.Threshold;

    /// <summary>JSON-serialized condition parameters (deserialized into <see cref="ConditionNode"/>).</summary>
    public string ConditionParams { get; set; } = "{}";

    /// <summary>Minutes to wait after condition clears before transitioning back to idle.</summary>
    public int HysteresisMinutes { get; set; }

    /// <summary>Severity level of alerts generated by this rule.</summary>
    /// <seealso cref="Alerts.AlertRuleSeverity"/>
    public AlertRuleSeverity Severity { get; set; } = AlertRuleSeverity.Warning;

    /// <summary>JSON-serialized client configuration (sound, vibration, display preferences).</summary>
    public string ClientConfiguration { get; set; } = "{}";

    /// <summary>Whether this rule is actively being evaluated.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>When the rule was last enabled; null when not recorded.</summary>
    public DateTime? EnabledAt { get; set; }

    /// <summary>Display order among rules.</summary>
    public int SortOrder { get; set; }

    /// <summary>Whether this rule auto-resolves (clears) when its condition no longer holds.</summary>
    public bool AutoResolveEnabled { get; set; }

    /// <summary>JSON-serialized auto-resolve parameters (e.g. delay, mode), or null if unused.</summary>
    public string? AutoResolveParams { get; set; }

    /// <summary>What an unknown root means to the tracker: one of <see cref="AlertRuleOnUnknown"/>.</summary>
    public string OnUnknown { get; set; } = AlertRuleOnUnknown.Hold;

    /// <summary>
    /// How many minutes old a reading may be before the rule's reading-driven leaves read it
    /// unknown; null is the engine's default (docs/alerts/engine-semantics.md §3).
    /// </summary>
    public int? GlucoseFreshnessMinutes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When this rule was last modified.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single continuous excursion (out-of-range episode) for an <see cref="AlertRule"/>.
/// </summary>
/// <seealso cref="AlertRule"/>
/// <seealso cref="AlertTrackerState"/>
public class AlertExcursion
{
    public Guid Id { get; set; }

    /// <summary>The <see cref="AlertRule"/> that triggered this excursion.</summary>
    public Guid AlertRuleId { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>When the excursion ended. Null if still active.</summary>
    public DateTime? EndedAt { get; set; }

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
    /// <summary>
    /// What the payload tells its recipients, so a webhook or other consumer can tell a notice
    /// that the rule cannot run from an excursion. Serialised as <c>"excursion"</c> or
    /// <c>"unevaluable"</c>; an <see cref="Alerts.AlertPayloadKind.Unevaluable"/> payload has no
    /// <see cref="GlucoseValue"/>, <see cref="Trend"/> or <see cref="TrendRate"/>, and its
    /// <see cref="ReadingTimestamp"/> is the time of the notice. Documented for webhook receivers
    /// in the portal's alert rule docs (docs/alerts/building-rules).
    /// </summary>
    public Alerts.AlertPayloadKind Kind { get; init; } = Alerts.AlertPayloadKind.Excursion;

    /// <summary>
    /// On a <see cref="Alerts.AlertPayloadKind.Held"/> or <see cref="Alerts.AlertPayloadKind.HeldClosed"/>
    /// notice, the facts the rule is waiting for; absent on every other kind.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Alerts.HeldFact>? HeldFacts { get; init; }

    /// <summary>
    /// On an excursion its evaluation opened, what each true <c>predicted</c>,
    /// <c>eventual_glucose</c> and <c>carbs_required</c> condition of the rule met its comparison
    /// with: the forecast curve and point, or the loop's value. Absent when there is none, and on a
    /// dispatch the sweep finishes for an evaluation that did not, which has no evaluation to read.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Alerts.AlertMetPoint>? MetBy { get; init; }

    /// <summary>
    /// The rule's condition type; null in the notice for a rule whose stored condition type is not
    /// one the engine knows.
    /// </summary>
    public required AlertConditionType? AlertType { get; init; }

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
