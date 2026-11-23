using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.API.Services.Alerts.Conditions;
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
    /// The ordinal sent for "this rule has an active excursion". The engine only checks that one is
    /// present and echoes it back. The host identifies an excursion by its row id, which the
    /// transition writer maps from the transition, so it stores no engine ordinal and sends no
    /// last ordinal: the engine reads the active one as the last, so an open never reuses it.
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
    /// Builds the FFI rule object from the stored rule row. Malformed stored condition JSON
    /// throws <see cref="JsonException"/> into the orchestrator's per-rule catch. Auto-resolve
    /// params are sent only when auto-resolve is enabled, and unparseable ones as null. They only
    /// gate auto-resolve, which the engine never fires for a tree that does not parse.
    /// </summary>
    public static RustAlertRule BuildRule(AlertRule rule) => new()
    {
        Id = rule.Id,
        ConditionType = AlertConditionTypeNames.ToWireString(rule.ConditionType),
        ConditionParams = ParseJson(rule.ConditionParams),
        HysteresisMinutes = rule.HysteresisMinutes,
        AutoResolveEnabled = rule.AutoResolveEnabled,
        AutoResolveParams = rule.AutoResolveEnabled ? RuleTrees.AutoResolve(rule.AutoResolveParams) : null,
        OnUnknown = rule.OnUnknown,
        GlucoseFreshnessMinutes = rule.GlucoseFreshnessMinutes,
        EnabledAt = rule.EnabledAt,
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
                ActiveExcursionOrdinal = state.ActiveExcursionId is null ? null : ActiveExcursionSentinel,
                UpdatedAt = DateTime.SpecifyKind(state.UpdatedAt, DateTimeKind.Utc),
                HysteresisStartedAt = Utc(state.HysteresisStartedAt),
                AwaitingRearm = state.AwaitingRearm,
                HeldSince = Utc(state.HeldSince),
            };

    public static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The full node <c>{"type": wire, wire: payload}</c> for a stored payload-only body, the payload
    /// copied verbatim; a blank body is JSON <c>null</c>.
    /// </summary>
    /// <exception cref="JsonException">The payload is not valid JSON.</exception>
    public static string WrapPayload(string wire, string? payloadJson)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", wire);
            writer.WritePropertyName(wire);
            if (string.IsNullOrWhiteSpace(payloadJson))
                writer.WriteNullValue();
            else
                writer.WriteRawValue(payloadJson);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Per-instance memo for <see cref="BuildContext"/>: the orchestrator passes the same
    /// enriched immutable <see cref="SensorContext"/> to every rule of a tick, so the wire element
    /// is built and serialised once per context instance. Keyed by reference; <see cref="ConditionalWeakTable{TKey,TValue}.GetValue"/>
    /// is thread-safe and entries are collected with their context.
    /// </summary>
    private static readonly ConditionalWeakTable<SensorContext, object> ContextElementCache = new();

    /// <summary>
    /// Copies one map of persisted sustained state into the envelope with every instant pinned to
    /// <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    /// <remarks>
    /// The crate requires an offset on every instant, and a <see cref="DateTimeKind.Unspecified"/>
    /// one serialises without it, failing the whole call. These are the one instants that reach
    /// the boundary straight from a store rather than through a projection that pins them.
    /// </remarks>
    public static Dictionary<string, DateTime> BuildTimers(IReadOnlyDictionary<string, DateTime> timers)
    {
        var wire = new Dictionary<string, DateTime>(timers.Count, StringComparer.Ordinal);
        foreach (var (path, at) in timers)
            wire[path] = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        return wire;
    }

    /// <summary>
    /// Projects a <see cref="SensorContext"/> onto the corpus <c>ScenarioContext</c> wire
    /// shape. Field-by-field total against the corpus's <c>ScenarioModels.cs</c>
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
            LatestReceivedAt = Utc(ctx.LatestReceivedAt),
            TrendRate = ctx.TrendRate,
            LastReadingAt = Utc(ctx.LastReadingAt),
            TrendBucket = ctx.TrendBucket is { } tb ? WireEnum(tb) : null,
            IobUnits = ctx.IobUnits,
            CobGrams = ctx.CobGrams,
            ReservoirUnits = ctx.ReservoirUnits,
            ReservoirIsLowerBound = ctx.ReservoirIsLowerBound ? true : null,
            LastSiteChangeAt = Utc(ctx.LastSiteChangeAt),
            LastSensorStartAt = Utc(ctx.LastSensorStartAt),
            Predictions = ctx.Predictions.Count == 0 ? null : WirePoints(ctx.Predictions),
            PredictionCurves = ctx.PredictionCurves.Count == 0
                ? null
                : ctx.PredictionCurves
                    .Where(kv => kv.Key != ForecastCurve.Default)
                    .ToDictionary(kv => WireEnum(kv.Key), kv => WirePoints(kv.Value), StringComparer.Ordinal),
            ApsDetermination = ctx.ApsDetermination is { } determination
                ? new WireApsDetermination(
                    Utc(determination.At)!.Value, determination.EventualMgdl, determination.CarbsRequiredGrams)
                : null,
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
            TransmitterVoltageA = ctx.TransmitterVoltageA,
            TransmitterVoltageB = ctx.TransmitterVoltageB,
            TransmitterResistance = ctx.TransmitterResistance,
            TransmitterDays = ctx.TransmitterDays,
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
            TargetLow = ctx.TargetLow,
            TargetHigh = ctx.TargetHigh,
            LastCarbAt = Utc(ctx.LastCarbAt),
            LastBolusAt = Utc(ctx.LastBolusAt),
            TenantTimeZoneId = ctx.TenantTimeZoneId is { Length: > 0 } tenantZone
                ? TimeZoneHelper.ToIanaIdIfWindows(tenantZone)
                : ctx.TenantTimeZoneId,
            ActivePumpModes = ctx.ActivePumpModes.Count == 0
                ? null
                : ctx.ActivePumpModes
                    .Select(m => new WirePumpMode(m.Mode, Utc(m.StartedAt)!.Value))
                    .ToList(),
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
                    .Select(kv => new WireTrackerReference(
                        kv.Key, Utc(kv.Value.ReferenceAt)!.Value, Utc(kv.Value.RecordedAt), kv.Value.IsScheduled))
                    .ToList(),
            SleepSessionActive = ctx.SleepSessionActive,
            SensorState = ctx.SensorState is { } sensor
                ? new WireSensorState(WireEnum(sensor.State), Utc(sensor.Since), Utc(sensor.Until))
                : null,
        };

        return JsonSerializer.SerializeToElement(wire, AlertEnvelopeJson.Options);
    }

    private static List<WirePrediction> WirePoints(IReadOnlyList<PredictedGlucosePoint> points) =>
        points.Select(p => new WirePrediction(p.OffsetMinutes, p.Mgdl)).ToList();

    // -----------------------------------------------------------------------
    // Response mapping
    // -----------------------------------------------------------------------

    /// <summary>
    /// The engine's <c>met_by</c> as typed points. An entry whose kind, curve or limit fact this host
    /// has no member for is dropped: it can only come from an engine newer than the host, and the payload
    /// member is optional.
    /// </summary>
    public static IReadOnlyList<AlertMetPoint> MetByFromWire(IReadOnlyList<RustMetPoint>? wire)
    {
        if (wire is null || wire.Count == 0)
            return [];
        var points = new List<AlertMetPoint>(wire.Count);
        foreach (var point in wire)
        {
            if (AlertConditionTypeNames.FromWireString(point.Kind) is not { } kind)
                continue;
            ForecastCurve? curve = null;
            if (point.Curve is { } curveName)
            {
                if (FromWireEnum<ForecastCurve>(curveName) is not { } known)
                    continue;
                curve = known;
            }
            AlertLimitFact? limitFact = null;
            if (point.LimitFact is { } factName)
            {
                if (FromWireEnum<AlertLimitFact>(factName) is not { } known)
                    continue;
                limitFact = known;
            }
            points.Add(new AlertMetPoint(
                point.LeafId, kind, curve, point.OffsetMinutes, point.Value, point.Limit, limitFact,
                limitFact is null ? null : point.LimitOffset));
        }
        return points;
    }

    private static TEnum? FromWireEnum<TEnum>(string wire) where TEnum : struct, Enum
    {
        try
        {
            return JsonSerializer.Deserialize<TEnum>($"\"{wire}\"", EnumWireOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ExcursionTransitionType TransitionFromWire(RustTransition wire) => wire switch
    {
        RustTransition.None => ExcursionTransitionType.None,
        RustTransition.Opened => ExcursionTransitionType.ExcursionOpened,
        RustTransition.Continues => ExcursionTransitionType.ExcursionContinues,
        RustTransition.HysteresisStarted => ExcursionTransitionType.HysteresisStarted,
        RustTransition.HysteresisResumed => ExcursionTransitionType.HysteresisResumed,
        RustTransition.Closed => ExcursionTransitionType.ExcursionClosed,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, null),
    };

    public static ExcursionCloseReason CloseReasonFromWire(RustCloseReason wire) => wire switch
    {
        RustCloseReason.Hysteresis => ExcursionCloseReason.Hysteresis,
        RustCloseReason.Auto => ExcursionCloseReason.AutoResolve,
        RustCloseReason.Manual => ExcursionCloseReason.Manual,
        RustCloseReason.RuleDisabled => ExcursionCloseReason.RuleDisabled,
        RustCloseReason.HoldBound => ExcursionCloseReason.HoldBound,
        _ => throw new ArgumentOutOfRangeException(nameof(wire), wire, null),
    };

    public static RustCloseReason CloseReasonToRust(ExcursionCloseReason reason) => reason switch
    {
        ExcursionCloseReason.Hysteresis => RustCloseReason.Hysteresis,
        ExcursionCloseReason.AutoResolve => RustCloseReason.Auto,
        ExcursionCloseReason.Manual => RustCloseReason.Manual,
        ExcursionCloseReason.RuleDisabled => RustCloseReason.RuleDisabled,
        ExcursionCloseReason.HoldBound => RustCloseReason.HoldBound,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    /// <summary>
    /// The engine's post-state as a <see cref="TrackerPostState"/>, or <see langword="null"/> when
    /// it holds no per-rule state. <paramref name="heldFacts"/> are kept only while it is held.
    /// </summary>
    public static TrackerPostState? PostStateFromWire(RustTrackerState tracker, string? heldFacts) =>
        tracker.State is null
            ? null
            : new TrackerPostState(
                tracker.State,
                tracker.ActiveExcursionOrdinal is not null,
                tracker.UpdatedAt!.Value,
                tracker.HysteresisStartedAt,
                tracker.AwaitingRearm,
                Utc(tracker.HeldSince),
                tracker.HeldSince is null ? null : heldFacts);

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
        [JsonPropertyName("latest_received_at")] public DateTime? LatestReceivedAt { get; init; }
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
        [JsonPropertyName("prediction_curves")] public Dictionary<string, List<WirePrediction>>? PredictionCurves { get; init; }
        [JsonPropertyName("aps_determination")] public WireApsDetermination? ApsDetermination { get; init; }
        [JsonPropertyName("active_alerts")] public List<WireActiveAlert>? ActiveAlerts { get; init; }
        [JsonPropertyName("last_aps_cycle_at")] public DateTime? LastApsCycleAt { get; init; }
        [JsonPropertyName("last_aps_enacted_at")] public DateTime? LastApsEnactedAt { get; init; }
        [JsonPropertyName("pump_battery_percent")] public decimal? PumpBatteryPercent { get; init; }
        [JsonPropertyName("active_temp_basal")] public WireTempBasal? ActiveTempBasal { get; init; }
        [JsonPropertyName("uploader_battery_percent")] public decimal? UploaderBatteryPercent { get; init; }
        [JsonPropertyName("transmitter_voltage_a")] public decimal? TransmitterVoltageA { get; init; }
        [JsonPropertyName("transmitter_voltage_b")] public decimal? TransmitterVoltageB { get; init; }
        [JsonPropertyName("transmitter_resistance")] public decimal? TransmitterResistance { get; init; }
        [JsonPropertyName("transmitter_days")] public decimal? TransmitterDays { get; init; }
        [JsonPropertyName("active_override")] public WireStartedSpan? ActiveOverride { get; init; }
        [JsonPropertyName("active_pump_suspension")] public WireStartedSpan? ActivePumpSuspension { get; init; }
        [JsonPropertyName("sensitivity_ratio")] public decimal? SensitivityRatio { get; init; }
        [JsonPropertyName("active_do_not_disturb")] public WireDoNotDisturb? ActiveDoNotDisturb { get; init; }
        [JsonPropertyName("has_ever_aps_cycled")] public bool HasEverApsCycled { get; init; }
        [JsonPropertyName("has_ever_pump_snapshot")] public bool HasEverPumpSnapshot { get; init; }
        [JsonPropertyName("has_ever_uploader_snapshot")] public bool HasEverUploaderSnapshot { get; init; }
        [JsonPropertyName("has_ever_aps_sensitivity")] public bool HasEverApsSensitivity { get; init; }
        [JsonPropertyName("glucose_bucket")] public string? GlucoseBucket { get; init; }
        [JsonPropertyName("target_low")] public decimal? TargetLow { get; init; }
        [JsonPropertyName("target_high")] public decimal? TargetHigh { get; init; }
        [JsonPropertyName("last_carb_at")] public DateTime? LastCarbAt { get; init; }
        [JsonPropertyName("last_bolus_at")] public DateTime? LastBolusAt { get; init; }
        [JsonPropertyName("tenant_time_zone_id")] public string? TenantTimeZoneId { get; init; }
        [JsonPropertyName("active_pump_modes")] public List<WirePumpMode>? ActivePumpModes { get; init; }
        [JsonPropertyName("active_state_spans")] public List<WireStateSpan>? ActiveStateSpans { get; init; }
        [JsonPropertyName("active_trackers")] public List<WireTrackerReference>? ActiveTrackers { get; init; }
        [JsonPropertyName("sleep_session_active")] public bool SleepSessionActive { get; init; }
        [JsonPropertyName("sensor_state")] public WireSensorState? SensorState { get; init; }
    }

    private sealed record WireSensorState(
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("since")] DateTime? Since,
        [property: JsonPropertyName("until")] DateTime? Until);

    private sealed record WireApsDetermination(
        [property: JsonPropertyName("at")] DateTime At,
        [property: JsonPropertyName("eventual_mgdl")] decimal? EventualMgdl,
        [property: JsonPropertyName("carbs_required_grams")] decimal? CarbsRequiredGrams);

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

    private sealed record WirePumpMode(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("started_at")] DateTime StartedAt);

    private sealed record WireStateSpan(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("started_at")] DateTime StartedAt);

    private sealed record WireTrackerReference(
        [property: JsonPropertyName("tracker_definition_id")] Guid TrackerDefinitionId,
        [property: JsonPropertyName("reference_at")] DateTime ReferenceAt,
        [property: JsonPropertyName("recorded_at")] DateTime? RecordedAt,
        [property: JsonPropertyName("scheduled")] bool Scheduled);
}
