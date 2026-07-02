using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Serialization contract for corpus scenario files (<c>*.json</c>) and expected
/// snapshots (<c>*.expected.json</c>). This shape is the interchange format consumed by
/// every parity engine (C# via this generator, Rust via <c>tests/parity.rs</c>, Kotlin
/// via the instrumented corpus test) — changes here are breaking changes to all of them
/// and must bump <see cref="ScenarioFile.SchemaVersion"/>.
/// </summary>
public static class CorpusJson
{
    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UtcDateTimeConverter());
        return options;
    }

    /// <summary>
    /// Writes UTC timestamps in the fixed round-trip form <c>yyyy-MM-ddTHH:mm:ssZ</c>
    /// (corpus scenarios are authored at whole-second precision) so regenerated output is
    /// byte-identical and trivially diffable.
    /// </summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetDateTime();
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException(
                    $"Corpus timestamps must be UTC; got Kind={value.Kind} for {value:O}");
            if (value.Ticks % TimeSpan.TicksPerSecond != 0)
                throw new InvalidOperationException(
                    $"Corpus timestamps must be whole-second; got {value:O}");
            writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
        }
    }
}

// ---------------------------------------------------------------------------
// Scenario input
// ---------------------------------------------------------------------------

public sealed record ScenarioFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required List<ScenarioRule> Rules { get; init; }

    public required List<ScenarioTick> Ticks { get; init; }
}

public sealed record ScenarioRule
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Wire-format condition type discriminator (e.g. <c>"composite"</c>, <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public required string ConditionType { get; init; }

    /// <summary>
    /// The kind-specific payload object exactly as stored in <c>alert_rules.condition_params</c>
    /// (the payload only, not a full ConditionNode — mirroring the DB column).
    /// </summary>
    [JsonPropertyName("condition_params")]
    public required JsonElement ConditionParams { get; init; }

    [JsonPropertyName("confirmation_readings")]
    public int ConfirmationReadings { get; init; } = 1;

    [JsonPropertyName("hysteresis_minutes")]
    public int HysteresisMinutes { get; init; }

    [JsonPropertyName("auto_resolve_enabled")]
    public bool AutoResolveEnabled { get; init; }

    /// <summary>A full ConditionNode object (<c>{"type": …, …}</c>), or null.</summary>
    [JsonPropertyName("auto_resolve_params")]
    public JsonElement? AutoResolveParams { get; init; }
}

public sealed record ScenarioTick
{
    /// <summary>The evaluation instant ("now") for this tick, UTC.</summary>
    public required DateTime At { get; init; }

    public required ScenarioContext Context { get; init; }
}

/// <summary>
/// JSON-friendly projection of <c>SensorContext</c>. Field names define the cross-engine
/// context wire format. Absent/null fields map to null on the SensorContext (and
/// <c>has_ever_*</c> flags default false).
/// </summary>
public sealed record ScenarioContext
{
    [JsonPropertyName("latest_value")] public decimal? LatestValue { get; init; }
    [JsonPropertyName("latest_timestamp")] public DateTime? LatestTimestamp { get; init; }
    [JsonPropertyName("trend_rate")] public decimal? TrendRate { get; init; }
    [JsonPropertyName("last_reading_at")] public DateTime? LastReadingAt { get; init; }
    /// <summary>Wire form of TrendBucket (<c>rising_fast</c> … <c>unknown</c>).</summary>
    [JsonPropertyName("trend_bucket")] public string? TrendBucket { get; init; }
    [JsonPropertyName("iob_units")] public decimal? IobUnits { get; init; }
    [JsonPropertyName("cob_grams")] public decimal? CobGrams { get; init; }
    [JsonPropertyName("reservoir_units")] public decimal? ReservoirUnits { get; init; }
    [JsonPropertyName("last_site_change_at")] public DateTime? LastSiteChangeAt { get; init; }
    [JsonPropertyName("last_sensor_start_at")] public DateTime? LastSensorStartAt { get; init; }
    public List<ScenarioPrediction>? Predictions { get; init; }
    [JsonPropertyName("active_alerts")] public List<ScenarioActiveAlert>? ActiveAlerts { get; init; }
    [JsonPropertyName("last_aps_cycle_at")] public DateTime? LastApsCycleAt { get; init; }
    [JsonPropertyName("last_aps_enacted_at")] public DateTime? LastApsEnactedAt { get; init; }
    [JsonPropertyName("pump_battery_percent")] public decimal? PumpBatteryPercent { get; init; }
    [JsonPropertyName("active_temp_basal")] public ScenarioTempBasal? ActiveTempBasal { get; init; }
    [JsonPropertyName("uploader_battery_percent")] public decimal? UploaderBatteryPercent { get; init; }
    [JsonPropertyName("active_override")] public ScenarioStartedSpan? ActiveOverride { get; init; }
    [JsonPropertyName("active_pump_suspension")] public ScenarioStartedSpan? ActivePumpSuspension { get; init; }
    [JsonPropertyName("sensitivity_ratio")] public decimal? SensitivityRatio { get; init; }
    [JsonPropertyName("active_do_not_disturb")] public ScenarioDoNotDisturb? ActiveDoNotDisturb { get; init; }
    [JsonPropertyName("has_ever_aps_cycled")] public bool HasEverApsCycled { get; init; }
    [JsonPropertyName("has_ever_pump_snapshot")] public bool HasEverPumpSnapshot { get; init; }
    [JsonPropertyName("has_ever_uploader_snapshot")] public bool HasEverUploaderSnapshot { get; init; }
    [JsonPropertyName("has_ever_aps_sensitivity")] public bool HasEverApsSensitivity { get; init; }
    /// <summary>Wire form of GlucoseBucket (<c>very_low</c> … <c>very_high</c>).</summary>
    [JsonPropertyName("glucose_bucket")] public string? GlucoseBucket { get; init; }
    [JsonPropertyName("last_carb_at")] public DateTime? LastCarbAt { get; init; }
    [JsonPropertyName("last_bolus_at")] public DateTime? LastBolusAt { get; init; }
    [JsonPropertyName("tenant_time_zone_id")] public string? TenantTimeZoneId { get; init; }
    [JsonPropertyName("active_pump_state")] public ScenarioPumpState? ActivePumpState { get; init; }
    [JsonPropertyName("active_state_spans")] public List<ScenarioStateSpan>? ActiveStateSpans { get; init; }
    [JsonPropertyName("active_trackers")] public List<ScenarioTrackerReference>? ActiveTrackers { get; init; }
}

public sealed record ScenarioPrediction(
    [property: JsonPropertyName("offset_minutes")] int OffsetMinutes,
    [property: JsonPropertyName("mgdl")] decimal Mgdl);

public sealed record ScenarioActiveAlert(
    [property: JsonPropertyName("alert_id")] Guid AlertId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("triggered_at")] DateTime TriggeredAt,
    [property: JsonPropertyName("acknowledged_at")] DateTime? AcknowledgedAt);

public sealed record ScenarioTempBasal(
    [property: JsonPropertyName("rate")] decimal Rate,
    [property: JsonPropertyName("scheduled_rate")] decimal? ScheduledRate,
    [property: JsonPropertyName("started_at")] DateTime StartedAt);

public sealed record ScenarioStartedSpan(
    [property: JsonPropertyName("started_at")] DateTime StartedAt);

public sealed record ScenarioDoNotDisturb(
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("source")] string Source);

/// <summary>Mode uses the PascalCase wire form of PumpModeState (<c>"Automatic"</c>, <c>"EaseOff"</c>, …).</summary>
public sealed record ScenarioPumpState(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("started_at")] DateTime StartedAt);

/// <summary>Category uses the PascalCase wire form of StateSpanCategory (<c>"Override"</c>, <c>"Sleep"</c>, …).</summary>
public sealed record ScenarioStateSpan(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("started_at")] DateTime StartedAt);

/// <summary>Active domain-tracker instance reference for the <c>tracker_age</c> leaf:
/// start time for Duration trackers, scheduled time for Event trackers.</summary>
public sealed record ScenarioTrackerReference(
    [property: JsonPropertyName("tracker_definition_id")] Guid TrackerDefinitionId,
    [property: JsonPropertyName("reference_at")] DateTime ReferenceAt);

// ---------------------------------------------------------------------------
// Expected snapshot output
// ---------------------------------------------------------------------------

public sealed record ExpectedFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    public required string Scenario { get; init; }

    public required List<ExpectedTick> Ticks { get; init; }
}

public sealed record ExpectedTick
{
    public required DateTime At { get; init; }

    public required List<ExpectedRuleResult> Rules { get; init; }
}

public sealed record ExpectedRuleResult
{
    [JsonPropertyName("rule_id")]
    public required Guid RuleId { get; init; }

    /// <summary>
    /// True when the orchestrator found no evaluator for the rule's root condition type
    /// (e.g. <c>signal_loss</c>) and skipped the rule without consulting the tracker.
    /// All other fields are absent in that case.
    /// </summary>
    public bool? Skipped { get; init; }

    /// <summary>Root condition truth for this tick.</summary>
    public bool? Root { get; init; }

    /// <summary>Per-leaf force-eval truths, ordered by leaf id (replay/leaf-log semantics).</summary>
    public List<ExpectedLeaf>? Leaves { get; init; }

    /// <summary>
    /// Tracker transition: <c>none | opened | continues | hysteresis_started |
    /// hysteresis_resumed | closed</c>.
    /// </summary>
    public string? Transition { get; init; }

    /// <summary>Close reason wire form (<c>hysteresis | auto | manual | …</c>) when transition is closed.</summary>
    [JsonPropertyName("close_reason")]
    public string? CloseReason { get; init; }

    public ExpectedTrackerState? Tracker { get; init; }

    /// <summary>True when the auto-resolve pass force-closed the excursion this tick.</summary>
    [JsonPropertyName("auto_resolved")]
    public bool? AutoResolved { get; init; }

    /// <summary>
    /// Sustained-timer store mutations performed during this rule's evaluation (root eval
    /// then auto-resolve eval), in execution order.
    /// </summary>
    [JsonPropertyName("timer_ops")]
    public List<ExpectedTimerOp>? TimerOps { get; init; }
}

public sealed record ExpectedLeaf(
    [property: JsonPropertyName("leaf_id")] int LeafId,
    [property: JsonPropertyName("value")] bool Value);

public sealed record ExpectedTrackerState
{
    public required string State { get; init; }

    [JsonPropertyName("confirmation_count")]
    public required int ConfirmationCount { get; init; }

    /// <summary>
    /// Stable ordinal of the active excursion within the scenario (1-based, in creation
    /// order), or null when no excursion is active. Ordinals rather than GUIDs keep the
    /// snapshot engine-independent.
    /// </summary>
    public int? Excursion { get; init; }
}

public sealed record ExpectedTimerOp(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] DateTime? At);
