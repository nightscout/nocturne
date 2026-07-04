using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Services.Alerts.Engines;

/// <summary>
/// Maps backend domain types onto the nocturne_alerts FFI envelope
/// (crates/nocturne-alerts-ffi/README.md) and back. The context wire shape is the
/// golden-corpus <c>ScenarioContext</c> format; the mapping must be total — every
/// <see cref="SensorContext"/> field an evaluator reads (engine-semantics §4) crosses
/// the boundary.
/// </summary>
/// <remarks>
/// Excursion identity: the FFI tracker carries per-scenario <em>ordinals</em>, but the
/// backend owns excursion GUIDs. The host threads only "has an active excursion" into
/// the request (a fixed sentinel ordinal) and treats the response
/// <c>result.transition</c> as the event source, mapping opened/closed onto
/// <c>IAlertTrackerRepository</c> rows itself — exactly the host strategy the envelope
/// README describes.
/// </remarks>
internal static class RustEnvelopeMapper
{
    /// <summary>
    /// Sentinel ordinal sent for "this rule has an active excursion". The actual value is
    /// irrelevant to the engine's semantics (it only checks presence and echoes it back);
    /// the next-ordinal is set above it so a same-call open never collides.
    /// </summary>
    private const int ActiveExcursionSentinel = 1;

    private static readonly JsonSerializerOptions EnumWireOptions = BuildEnumWireOptions();

    private static JsonSerializerOptions BuildEnumWireOptions()
    {
        // TrendBucket carries JsonStringEnumMemberName attributes but no type-level
        // converter (the live engine never serialises it — it lives on the SensorContext).
        // GlucoseBucket / StateSpanCategory / PumpModeState have type-level converters.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter<TrendBucket>());
        return options;
    }

    // -----------------------------------------------------------------------
    // Request building
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the FFI rule object from the stored rule row. Malformed stored JSON throws
    /// <see cref="JsonException"/> — matching the managed path, where the evaluator's
    /// payload deserialisation would throw into the per-rule catch.
    /// </summary>
    public static RustAlertRule BuildRule(AlertRule rule) => new()
    {
        Id = rule.Id,
        ConditionType = AlertConditionTypeNames.ToWireString(rule.ConditionType),
        ConditionParams = ParseJson(rule.ConditionParams),
        ConfirmationReadings = rule.ConfirmationReadings,
        HysteresisMinutes = rule.HysteresisMinutes,
        AutoResolveEnabled = rule.AutoResolveEnabled,
        AutoResolveParams = string.IsNullOrWhiteSpace(rule.AutoResolveParams)
            ? null
            : ParseJson(rule.AutoResolveParams!),
    };

    /// <summary>
    /// Maps persisted tracker state into the envelope's tracker object. The stored GUID
    /// collapses to the sentinel ordinal; the caller re-associates GUIDs from the
    /// response transition.
    /// </summary>
    public static RustTrackerState? BuildTracker(AlertTrackerState? state) =>
        state is null
            ? null
            : new RustTrackerState
            {
                State = state.State,
                ConfirmationCount = state.ConfirmationCount,
                ActiveExcursionOrdinal = state.ActiveExcursionId is null ? null : ActiveExcursionSentinel,
                UpdatedAt = DateTime.SpecifyKind(state.UpdatedAt, DateTimeKind.Utc),
                NextExcursionOrdinal = ActiveExcursionSentinel + 1,
            };

    public static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Per-instance memo for <see cref="BuildContext"/>: the orchestrator (and the shadow
    /// evaluator) pass the same enriched immutable <see cref="SensorContext"/> to every
    /// rule of a tick, so the wire element is built and serialised once per context
    /// instance. Keyed by reference; <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/>
    /// is thread-safe and entries are collected with their context.
    /// </summary>
    private static readonly ConditionalWeakTable<SensorContext, object> ContextElementCache = new();

    /// <summary>
    /// Projects a <see cref="SensorContext"/> onto the corpus <c>ScenarioContext</c> wire
    /// shape. Field-by-field total against the generator's <c>ScenarioModels.cs</c>
    /// (its <c>ToSensorContext</c> is the inverse of this mapping).
    /// </summary>
    public static JsonElement BuildContext(SensorContext ctx) => (JsonElement)BuildContextBoxed(ctx);

    /// <summary>Memoised boxed element; internal so tests can assert reference identity.</summary>
    internal static object BuildContextBoxed(SensorContext ctx) =>
        ContextElementCache.GetValue(ctx, static c => BuildContextElement(c));

    private static object BuildContextElement(SensorContext ctx)
    {
        var wire = new WireContext
        {
            LatestValue = ctx.LatestValue,
            LatestTimestamp = Utc(ctx.LatestTimestamp),
            TrendRate = ctx.TrendRate,
            LastReadingAt = Utc(ctx.LastReadingAt),
            TrendBucket = ctx.TrendBucket is { } tb ? WireEnum(tb) : null,
            IobUnits = ctx.IobUnits,
            CobGrams = ctx.CobGrams,
            ReservoirUnits = ctx.ReservoirUnits,
            ReservoirIsLowerBound = ctx.ReservoirIsLowerBound ? true : null,
            LastSiteChangeAt = Utc(ctx.LastSiteChangeAt),
            LastSensorStartAt = Utc(ctx.LastSensorStartAt),
            Predictions = ctx.Predictions.Count == 0
                ? null
                : ctx.Predictions.Select(p => new WirePrediction(p.OffsetMinutes, p.Mgdl)).ToList(),
            ActiveAlerts = ctx.ActiveAlerts.Count == 0
                ? null
                : ctx.ActiveAlerts
                    .Select(kv => new WireActiveAlert(
                        kv.Key, kv.Value.State, Utc(kv.Value.TriggeredAt)!.Value, Utc(kv.Value.AcknowledgedAt)))
                    .ToList(),
            LastApsCycleAt = Utc(ctx.LastApsCycleAt),
            LastApsEnactedAt = Utc(ctx.LastApsEnactedAt),
            PumpBatteryPercent = ctx.PumpBatteryPercent,
            ActiveTempBasal = ctx.ActiveTempBasal is { } tbasal
                ? new WireTempBasal(tbasal.Rate, tbasal.ScheduledRate, Utc(tbasal.StartedAt)!.Value)
                : null,
            UploaderBatteryPercent = ctx.UploaderBatteryPercent,
            ActiveOverride = ctx.ActiveOverride is { } ov
                ? new WireStartedSpan(Utc(ov.StartedAt)!.Value)
                : null,
            ActivePumpSuspension = ctx.ActivePumpSuspension is { } ps
                ? new WireStartedSpan(Utc(ps.StartedAt)!.Value)
                : null,
            SensitivityRatio = ctx.SensitivityRatio,
            ActiveDoNotDisturb = ctx.ActiveDoNotDisturb is { } dnd
                ? new WireDoNotDisturb(Utc(dnd.StartedAt)!.Value, dnd.Source)
                : null,
            HasEverApsCycled = ctx.HasEverApsCycled,
            HasEverPumpSnapshot = ctx.HasEverPumpSnapshot,
            HasEverUploaderSnapshot = ctx.HasEverUploaderSnapshot,
            HasEverApsSensitivity = ctx.HasEverApsSensitivity,
            GlucoseBucket = ctx.GlucoseBucket is { } gb ? WireEnum(gb) : null,
            LastCarbAt = Utc(ctx.LastCarbAt),
            LastBolusAt = Utc(ctx.LastBolusAt),
            TenantTimeZoneId = ctx.TenantTimeZoneId,
            ActivePumpState = ctx.ActivePumpState is { } pump
                ? new WirePumpState(WireEnum(pump.Mode), Utc(pump.StartedAt)!.Value)
                : null,
            // Serialise from the dictionary KEYS (not the snapshot's echoed fields) so the
            // engine reconstructs the exact same (category, state) lookup — including
            // null-state "any of category" keys.
            ActiveStateSpans = ctx.ActiveStateSpans.Count == 0
                ? null
                : ctx.ActiveStateSpans
                    .Select(kv => new WireStateSpan(
                        WireEnum(kv.Key.Category), kv.Key.State, Utc(kv.Value.StartedAt)!.Value))
                    .ToList(),
            ActiveTrackers = ctx.ActiveTrackers.Count == 0
                ? null
                : ctx.ActiveTrackers
                    .Select(kv => new WireTrackerReference(kv.Key, Utc(kv.Value)!.Value))
                    .ToList(),
        };

        return JsonSerializer.SerializeToElement(wire, AlertEnvelopeJson.Options);
    }

    /// <summary>
    /// Serialises a <see cref="ConditionNode"/> tree into the full-node JSON the FFI
    /// expects (child <c>type</c> strings preserved verbatim).
    /// </summary>
    public static JsonElement BuildNode(ConditionNode node) =>
        JsonSerializer.SerializeToElement(node, EvaluatorJson.Options);

    // -----------------------------------------------------------------------
    // Response mapping
    // -----------------------------------------------------------------------

    public static ExcursionTransitionType TransitionFromWire(string? wire) => wire switch
    {
        "none" or null => ExcursionTransitionType.None,
        "opened" => ExcursionTransitionType.ExcursionOpened,
        "continues" => ExcursionTransitionType.ExcursionContinues,
        "hysteresis_started" => ExcursionTransitionType.HysteresisStarted,
        "hysteresis_resumed" => ExcursionTransitionType.HysteresisResumed,
        "closed" => ExcursionTransitionType.ExcursionClosed,
        _ => throw new InvalidOperationException($"Unknown transition wire value '{wire}'"),
    };

    public static ExcursionCloseReason? CloseReasonFromWire(string? wire) => wire switch
    {
        null => null,
        "hysteresis" => ExcursionCloseReason.Hysteresis,
        "auto" => ExcursionCloseReason.AutoResolve,
        "manual" => ExcursionCloseReason.Manual,
        _ => throw new InvalidOperationException($"Unknown close reason wire value '{wire}'"),
    };

    /// <summary>Wire form of a managed transition type (for shadow comparison logging).</summary>
    public static string TransitionToWire(ExcursionTransitionType type) => type switch
    {
        ExcursionTransitionType.None => "none",
        ExcursionTransitionType.ExcursionOpened => "opened",
        ExcursionTransitionType.ExcursionContinues => "continues",
        ExcursionTransitionType.HysteresisStarted => "hysteresis_started",
        ExcursionTransitionType.HysteresisResumed => "hysteresis_resumed",
        ExcursionTransitionType.ExcursionClosed => "closed",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>Wire form of a managed close reason (for shadow comparison logging).</summary>
    public static string? CloseReasonToWire(ExcursionCloseReason? reason) => reason switch
    {
        null => null,
        ExcursionCloseReason.Hysteresis => "hysteresis",
        ExcursionCloseReason.AutoResolve => "auto",
        ExcursionCloseReason.Manual => "manual",
        _ => reason.ToString()!.ToLowerInvariant(),
    };

    private static DateTime? Utc(DateTime? value) =>
        value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static string WireEnum<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, EnumWireOptions).Trim('"');

    // -----------------------------------------------------------------------
    // Context wire DTOs (ScenarioContext shape; see ScenarioModels.cs)
    // -----------------------------------------------------------------------

    private sealed record WireContext
    {
        [JsonPropertyName("latest_value")] public decimal? LatestValue { get; init; }
        [JsonPropertyName("latest_timestamp")] public DateTime? LatestTimestamp { get; init; }
        [JsonPropertyName("trend_rate")] public decimal? TrendRate { get; init; }
        [JsonPropertyName("last_reading_at")] public DateTime? LastReadingAt { get; init; }
        [JsonPropertyName("trend_bucket")] public string? TrendBucket { get; init; }
        [JsonPropertyName("iob_units")] public decimal? IobUnits { get; init; }
        [JsonPropertyName("cob_grams")] public decimal? CobGrams { get; init; }
        [JsonPropertyName("reservoir_units")] public decimal? ReservoirUnits { get; init; }
        [JsonPropertyName("reservoir_is_lower_bound")] public bool? ReservoirIsLowerBound { get; init; }
        [JsonPropertyName("last_site_change_at")] public DateTime? LastSiteChangeAt { get; init; }
        [JsonPropertyName("last_sensor_start_at")] public DateTime? LastSensorStartAt { get; init; }
        [JsonPropertyName("predictions")] public List<WirePrediction>? Predictions { get; init; }
        [JsonPropertyName("active_alerts")] public List<WireActiveAlert>? ActiveAlerts { get; init; }
        [JsonPropertyName("last_aps_cycle_at")] public DateTime? LastApsCycleAt { get; init; }
        [JsonPropertyName("last_aps_enacted_at")] public DateTime? LastApsEnactedAt { get; init; }
        [JsonPropertyName("pump_battery_percent")] public decimal? PumpBatteryPercent { get; init; }
        [JsonPropertyName("active_temp_basal")] public WireTempBasal? ActiveTempBasal { get; init; }
        [JsonPropertyName("uploader_battery_percent")] public decimal? UploaderBatteryPercent { get; init; }
        [JsonPropertyName("active_override")] public WireStartedSpan? ActiveOverride { get; init; }
        [JsonPropertyName("active_pump_suspension")] public WireStartedSpan? ActivePumpSuspension { get; init; }
        [JsonPropertyName("sensitivity_ratio")] public decimal? SensitivityRatio { get; init; }
        [JsonPropertyName("active_do_not_disturb")] public WireDoNotDisturb? ActiveDoNotDisturb { get; init; }
        [JsonPropertyName("has_ever_aps_cycled")] public bool HasEverApsCycled { get; init; }
        [JsonPropertyName("has_ever_pump_snapshot")] public bool HasEverPumpSnapshot { get; init; }
        [JsonPropertyName("has_ever_uploader_snapshot")] public bool HasEverUploaderSnapshot { get; init; }
        [JsonPropertyName("has_ever_aps_sensitivity")] public bool HasEverApsSensitivity { get; init; }
        [JsonPropertyName("glucose_bucket")] public string? GlucoseBucket { get; init; }
        [JsonPropertyName("last_carb_at")] public DateTime? LastCarbAt { get; init; }
        [JsonPropertyName("last_bolus_at")] public DateTime? LastBolusAt { get; init; }
        [JsonPropertyName("tenant_time_zone_id")] public string? TenantTimeZoneId { get; init; }
        [JsonPropertyName("active_pump_state")] public WirePumpState? ActivePumpState { get; init; }
        [JsonPropertyName("active_state_spans")] public List<WireStateSpan>? ActiveStateSpans { get; init; }
        [JsonPropertyName("active_trackers")] public List<WireTrackerReference>? ActiveTrackers { get; init; }
    }

    private sealed record WirePrediction(
        [property: JsonPropertyName("offset_minutes")] int OffsetMinutes,
        [property: JsonPropertyName("mgdl")] decimal Mgdl);

    private sealed record WireActiveAlert(
        [property: JsonPropertyName("alert_id")] Guid AlertId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("triggered_at")] DateTime TriggeredAt,
        [property: JsonPropertyName("acknowledged_at")] DateTime? AcknowledgedAt);

    private sealed record WireTempBasal(
        [property: JsonPropertyName("rate")] decimal Rate,
        [property: JsonPropertyName("scheduled_rate")] decimal? ScheduledRate,
        [property: JsonPropertyName("started_at")] DateTime StartedAt);

    private sealed record WireStartedSpan(
        [property: JsonPropertyName("started_at")] DateTime StartedAt);

    private sealed record WireDoNotDisturb(
        [property: JsonPropertyName("started_at")] DateTime StartedAt,
        [property: JsonPropertyName("source")] string Source);

    private sealed record WirePumpState(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("started_at")] DateTime StartedAt);

    private sealed record WireStateSpan(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("started_at")] DateTime StartedAt);

    private sealed record WireTrackerReference(
        [property: JsonPropertyName("tracker_definition_id")] Guid TrackerDefinitionId,
        [property: JsonPropertyName("reference_at")] DateTime ReferenceAt);
}
