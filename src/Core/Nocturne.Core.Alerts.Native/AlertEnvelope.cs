using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Alerts.Native;

/// <summary>
/// Serialization contract for the nocturne_alerts FFI envelope
/// (crates/nocturne-alerts-ffi/README.md): snake_case property names,
/// case-insensitive reads, nulls omitted, UTC RFC 3339 timestamps.
/// </summary>
public static class AlertEnvelopeJson
{
    /// <summary>The envelope <c>schema_version</c> this binding speaks, in both directions.</summary>
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UtcDateTimeConverter());
        return options;
    }

    /// <summary>
    /// The Rust side (chrono) only accepts RFC 3339 instants with an explicit
    /// offset, so every DateTime crossing the boundary is written as UTC with
    /// a <c>Z</c> suffix (sub-second precision preserved when present).
    /// </summary>
    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetDateTime();
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
            writer.WriteStringValue(utc);
        }
    }
}

/// <summary>
/// An alert rule in the FFI request envelope — the corpus <c>ScenarioRule</c>
/// wire shape (a stored rule row: payload-only condition_params plus tracker
/// and auto-resolve configuration).
/// </summary>
public sealed record RustAlertRule
{
    public required Guid Id { get; init; }

    /// <summary>Wire-format condition type discriminator (e.g. <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public required string ConditionType { get; init; }

    /// <summary>
    /// The kind-specific payload object exactly as stored in
    /// <c>alert_rules.condition_params</c> (JSON null allowed).
    /// </summary>
    [JsonPropertyName("condition_params")]
    public required JsonElement ConditionParams { get; init; }

    [JsonPropertyName("hysteresis_minutes")]
    public int HysteresisMinutes { get; init; }

    [JsonPropertyName("auto_resolve_enabled")]
    public bool AutoResolveEnabled { get; init; }

    /// <summary>A full ConditionNode object (<c>{"type": …, …}</c>), or null.</summary>
    [JsonPropertyName("auto_resolve_params")]
    public JsonElement? AutoResolveParams { get; init; }

    /// <summary>
    /// What an unknown root means to the tracker: <c>hold</c>, <c>met</c> or <c>not_met</c>
    /// (docs/alerts/engine-semantics.md §6.4). Any other word is an envelope error.
    /// </summary>
    [JsonPropertyName("on_unknown")]
    public string OnUnknown { get; init; } = "hold";

    /// <summary>
    /// How many minutes old a reading may be before the rule's reading-driven leaves read it
    /// unknown (docs/alerts/engine-semantics.md §3); null is the engine's default. A value outside
    /// 1 to 1440 is an envelope error.
    /// </summary>
    [JsonPropertyName("glucose_freshness_minutes")]
    public int? GlucoseFreshnessMinutes { get; init; }

    /// <summary>When the rule was last enabled (docs/alerts/engine-semantics.md §3).</summary>
    [JsonPropertyName("enabled_at")]
    public DateTime? EnabledAt { get; init; }
}

/// <summary>
/// One rule's persisted tracker state, carried in and out of every evaluate call. Its fields are
/// absent until the rule has been evaluated at least once.
/// </summary>
public sealed record RustTrackerState
{
    /// <summary><c>idle | active | hysteresis</c>, or null when no state exists yet.</summary>
    public string? State { get; init; }

    /// <summary>1-based ordinal of the active excursion, when one is active.</summary>
    [JsonPropertyName("active_excursion_ordinal")]
    public int? ActiveExcursionOrdinal { get; init; }

    /// <summary>Required whenever <see cref="State"/> is present.</summary>
    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; init; }

    /// <summary>
    /// When the excursion entered hysteresis; present only in the <c>hysteresis</c> state. The
    /// engine adopts <see cref="UpdatedAt"/> once when it is missing there.
    /// </summary>
    [JsonPropertyName("hysteresis_started_at")]
    public DateTime? HysteresisStartedAt { get; init; }

    /// <summary>
    /// Idle after an auto-resolve closed an excursion whose condition still held; the wire
    /// carries it only when true.
    /// </summary>
    [JsonPropertyName("awaiting_rearm")]
    public bool AwaitingRearm { get; init; }

    /// <summary>
    /// The ordinal of the excursion the rule opened last; the wire carries it once the rule has
    /// opened one. A rule numbers its own excursions 1, 2, ... (docs/alerts/engine-semantics.md §7).
    /// </summary>
    [JsonPropertyName("last_excursion_ordinal")]
    public int? LastExcursionOrdinal { get; init; }

    /// <summary>
    /// When the rule's current hold began (docs/alerts/engine-semantics.md §6.4); present only
    /// while it is held.
    /// </summary>
    [JsonPropertyName("held_since")]
    public DateTime? HeldSince { get; init; }
}

/// <summary>Request envelope for <c>nocturne_alerts_evaluate</c>.</summary>
public sealed record RustEvaluateRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    public required RustAlertRule Rule { get; init; }

    /// <summary>SensorContext snapshot in the corpus <c>ScenarioContext</c> wire shape.</summary>
    public required JsonElement Context { get; init; }

    /// <summary>The evaluation instant ("now"), UTC.</summary>
    public required DateTime Now { get; init; }

    /// <summary>Persisted sustained runs for this rule: condition path → first-true instant.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

    /// <summary>Persisted sustained paths with no run: condition path → last not-true instant.</summary>
    [JsonPropertyName("not_true")]
    public Dictionary<string, DateTime>? NotTrue { get; init; }

    public RustTrackerState? Tracker { get; init; }

    /// <summary>
    /// Whether <c>result.leaves</c> is produced, each leaf evaluated alone. Off unless asked for.
    /// </summary>
    [JsonPropertyName("include_leaves")]
    public bool IncludeLeaves { get; init; }
}

/// <summary>The fields every nocturne_alerts response envelope carries, success or error.</summary>
public interface IRustResponseEnvelope
{
    int SchemaVersion { get; }

    bool Ok { get; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    string? Error { get; }
}

/// <summary>Response envelope for <c>nocturne_alerts_evaluate</c>.</summary>
public sealed record RustEvaluateResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The rule outcome in the corpus <c>ExpectedRuleResult</c> wire shape
    /// (root, leaves, transition, close_reason, tracker, auto_resolved, timer_ops).
    /// </summary>
    public JsonElement? Result { get; init; }

    /// <summary>Post-evaluation sustained runs for this rule.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

    /// <summary>Post-evaluation sustained paths with no run, with their last not-true instant.</summary>
    [JsonPropertyName("not_true")]
    public Dictionary<string, DateTime>? NotTrue { get; init; }

    /// <summary>Post-evaluation tracker state to persist.</summary>
    public RustTrackerState? Tracker { get; init; }
}

/// <summary>
/// Typed view of <see cref="RustEvaluateResponse.Result"/> — the corpus
/// <c>ExpectedRuleResult</c> wire shape. Read it through <see cref="RustAlertEngine.GetRuleResult"/>,
/// which rejects a result missing a field its shape requires.
/// </summary>
public sealed record RustRuleResult
{
    [JsonPropertyName("rule_id"), JsonRequired]
    public Guid RuleId { get; init; }

    /// <summary>The rule was skipped; the wire carries <c>skipped</c> only when true.</summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// Root condition truth; present on every result that was not skipped, and false when
    /// <see cref="Unknown"/>.
    /// </summary>
    public bool? Root { get; init; }

    /// <summary>
    /// The root is unknown (docs/alerts/engine-semantics.md §2.4); the wire carries it only when
    /// true.
    /// </summary>
    public bool Unknown { get; init; }

    /// <summary>
    /// The context facts whose unknown reached the root, each with its reason; present exactly
    /// when <see cref="Unknown"/>.
    /// </summary>
    [JsonPropertyName("unknown_facts")]
    public List<RustUnknownFact>? UnknownFacts { get; init; }

    /// <summary>
    /// Per-leaf force-eval truths, ascending by leaf id; present on every result that was not
    /// skipped when the request set <see cref="RustEvaluateRequest.IncludeLeaves"/>.
    /// </summary>
    public List<RustLeafValue>? Leaves { get; init; }

    /// <summary>Tracker transition; present on every result that was not skipped.</summary>
    public RustTransition? Transition { get; init; }

    /// <summary>Present exactly when <see cref="Transition"/> is <see cref="RustTransition.Closed"/>.</summary>
    [JsonPropertyName("close_reason")]
    public RustCloseReason? CloseReason { get; init; }

    /// <summary>
    /// This evaluation's hold reached the engine's bound, once per hold
    /// (docs/alerts/engine-semantics.md §6.4); the wire carries it only when true.
    /// </summary>
    [JsonPropertyName("held_too_long")]
    public bool HeldTooLong { get; init; }

    /// <summary>The auto-resolve pass force-closed the excursion; the wire carries it only when true.</summary>
    [JsonPropertyName("auto_resolved")]
    public bool AutoResolved { get; init; }

    /// <summary>Sustained-timer mutations performed during the call, in execution order; omitted when none.</summary>
    [JsonPropertyName("timer_ops")]
    public List<RustTimerOp>? TimerOps { get; init; }

    /// <summary>
    /// When an evaluation from the returned state can next differ with only the clock moving
    /// (docs/alerts/engine-semantics.md §5.3); omitted when none can.
    /// </summary>
    [JsonPropertyName("next_change_at")]
    public DateTime? NextChangeAt { get; init; }

    /// <summary>
    /// The anchors the evaluation read more than 5 minutes ahead of <c>now</c>
    /// (docs/alerts/engine-semantics.md §5.4); omitted when none was.
    /// </summary>
    [JsonPropertyName("clock_anomaly")]
    public List<RustClockAnomaly>? ClockAnomaly { get; init; }

    /// <summary>
    /// What each true <c>predicted</c>, <c>eventual_glucose</c> and <c>carbs_required</c> leaf of the
    /// body met its comparison with (docs/alerts/engine-semantics.md §3, met by); omitted when none
    /// did. Reported whether or not the leaf log was requested.
    /// </summary>
    [JsonPropertyName("met_by")]
    public List<RustMetPoint>? MetBy { get; init; }
}

/// <summary>
/// One entry of <see cref="RustRuleResult.MetBy"/>: the leaf, its kind as its wire name, and for
/// <c>predicted</c> the curve and offset of the point; <c>value</c> is mg/dL, or grams for
/// <c>carbs_required</c>. For a limit from a fact, <c>limit</c> is the limit compared against,
/// <c>limit_fact</c> the fact's wire name and <c>limit_offset</c> the rule's offset to it, absent when 0.
/// </summary>
public sealed record RustMetPoint(
    [property: JsonPropertyName("leaf_id"), JsonRequired] int LeafId,
    [property: JsonPropertyName("kind"), JsonRequired] string Kind,
    [property: JsonPropertyName("curve")] string? Curve,
    [property: JsonPropertyName("offset_minutes")] int? OffsetMinutes,
    [property: JsonPropertyName("value"), JsonRequired] decimal Value,
    [property: JsonPropertyName("limit")] decimal? Limit = null,
    [property: JsonPropertyName("limit_fact")] string? LimitFact = null,
    [property: JsonPropertyName("limit_offset")] decimal? LimitOffset = null);

/// <summary>
/// One anchor read beyond the clock tolerance: the context field or state that held it, and how
/// far ahead of <c>now</c> it was in whole milliseconds.
/// </summary>
public sealed record RustClockAnomaly(
    [property: JsonPropertyName("fact"), JsonRequired] string Fact,
    [property: JsonPropertyName("ahead_ms"), JsonRequired] long AheadMs);

/// <summary>
/// A context fact (docs/alerts/engine-semantics.md §5.2) whose unknown reached an unknown root, and
/// why it was unknown: <c>absent</c>, <c>stale</c>, <c>ahead_of_clock</c>, <c>unrecognised</c> or
/// <c>inexact</c>.
/// </summary>
public sealed record RustUnknownFact(
    [property: JsonPropertyName("fact"), JsonRequired] string Fact,
    [property: JsonPropertyName("reason"), JsonRequired] string Reason);

/// <summary>Tracker transition wire values in <see cref="RustRuleResult.Transition"/>.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<RustTransition>))]
public enum RustTransition
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("opened")] Opened,
    [JsonStringEnumMemberName("continues")] Continues,
    [JsonStringEnumMemberName("hysteresis_started")] HysteresisStarted,
    [JsonStringEnumMemberName("hysteresis_resumed")] HysteresisResumed,
    [JsonStringEnumMemberName("closed")] Closed,
}

/// <summary>Close reason wire values in <see cref="RustRuleResult.CloseReason"/>.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<RustCloseReason>))]
public enum RustCloseReason
{
    [JsonStringEnumMemberName("hysteresis")] Hysteresis,
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("rule-disabled")] RuleDisabled,
    [JsonStringEnumMemberName("hold-bound")] HoldBound,
}

/// <summary>
/// Reads only the declared member names; an integer or an unknown name is a
/// <see cref="JsonException"/>, so a value this binding does not know never maps to a default.
/// </summary>
public sealed class StrictStringEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(null, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>
/// One per-leaf force-eval truth in <see cref="RustRuleResult.Leaves"/>; <see cref="Value"/> is
/// false when <see cref="Unknown"/>.
/// </summary>
public sealed record RustLeafValue(
    [property: JsonPropertyName("leaf_id")] int LeafId,
    [property: JsonPropertyName("value")] bool Value,
    [property: JsonPropertyName("unknown")] bool Unknown = false);

/// <summary>One sustained-path mutation.</summary>
public sealed record RustTimerOp(
    [property: JsonPropertyName("op"), JsonRequired] RustTimerOpKind Op,
    [property: JsonPropertyName("path"), JsonRequired] string Path,
    [property: JsonPropertyName("at"), JsonRequired] DateTime At);

/// <summary>Timer op wire values in <see cref="RustTimerOp.Op"/>.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<RustTimerOpKind>))]
public enum RustTimerOpKind
{
    /// <summary>The path's run started at <see cref="RustTimerOp.At"/>.</summary>
    [JsonStringEnumMemberName("set")] Set,

    /// <summary>The path has no run; its child was last found not true at <see cref="RustTimerOp.At"/>.</summary>
    [JsonStringEnumMemberName("clear")] Clear,
}

/// <summary>Request envelope for <c>nocturne_alerts_evaluate_node</c>.</summary>
public sealed record RustEvaluateNodeRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>The rule whose timer rows the tree's sustained nodes are keyed under.</summary>
    [JsonPropertyName("rule_id")]
    public required Guid RuleId { get; init; }

    /// <summary>A full ConditionNode object (<c>{"type": …, …}</c>).</summary>
    public required JsonElement Node { get; init; }

    /// <summary>Root path segment override (e.g. <c>"snooze"</c>, <c>"auto_resolve"</c>); null = the node's verbatim type.</summary>
    public string? Root { get; init; }

    /// <summary>SensorContext snapshot in the corpus <c>ScenarioContext</c> wire shape.</summary>
    public required JsonElement Context { get; init; }

    /// <summary>The evaluation instant ("now"), UTC.</summary>
    public required DateTime Now { get; init; }

    /// <summary>Persisted sustained runs for the rule: condition path → first-true instant.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

    /// <summary>Persisted sustained paths with no run: condition path → last not-true instant.</summary>
    [JsonPropertyName("not_true")]
    public Dictionary<string, DateTime>? NotTrue { get; init; }
}

/// <summary>Response envelope for <c>nocturne_alerts_evaluate_node</c>.</summary>
public sealed record RustEvaluateNodeResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>The node's truth; present on every successful response, and false when <see cref="Unknown"/>.</summary>
    public bool? Value { get; init; }

    /// <summary>The node is unknown; the wire carries it only when true.</summary>
    public bool Unknown { get; init; }

    /// <summary>Post-evaluation sustained runs for the rule.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

    /// <summary>Post-evaluation sustained paths with no run, with their last not-true instant.</summary>
    [JsonPropertyName("not_true")]
    public Dictionary<string, DateTime>? NotTrue { get; init; }

    /// <summary>Sustained-timer mutations performed during the call, in execution order.</summary>
    [JsonPropertyName("timer_ops")]
    public List<RustTimerOp>? TimerOps { get; init; }

    /// <summary>As <see cref="RustRuleResult.ClockAnomaly"/>; omitted when none.</summary>
    [JsonPropertyName("clock_anomaly")]
    public List<RustClockAnomaly>? ClockAnomaly { get; init; }
}

/// <summary>Response envelope for <c>nocturne_alerts_leaf_paths</c>.</summary>
public sealed record RustLeafPathsResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>The root path segment the paths are rooted at.</summary>
    public string? Root { get; init; }

    /// <summary>Canonical path of every node slot in the tree, in document order.</summary>
    public List<string>? Paths { get; init; }

    /// <summary>Leaf-id → path pairs (pre-order leaf ids, docs/alerts/engine-semantics.md §2.2).</summary>
    public List<RustLeafPath>? Leaves { get; init; }
}

public sealed record RustLeafPath(
    [property: JsonPropertyName("leaf_id")] int LeafId,
    [property: JsonPropertyName("path")] string Path);

/// <summary>
/// Request envelope for <c>nocturne_alerts_classify</c>: a rule's root condition
/// type plus its payload-only <c>condition_params</c> (the
/// <c>alert_rules.condition_params</c> shape), from which the engine derives the
/// scope class for scoped Do Not Disturb (ADR 0004).
/// </summary>
public sealed record RustClassifyRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>Wire-format condition type discriminator (e.g. <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public required string ConditionType { get; init; }

    /// <summary>
    /// The kind-specific payload object exactly as stored in
    /// <c>alert_rules.condition_params</c> (JSON null allowed).
    /// </summary>
    [JsonPropertyName("condition_params")]
    public required JsonElement ConditionParams { get; init; }
}

/// <summary>Response envelope for <c>nocturne_alerts_classify</c>.</summary>
public sealed record RustClassifyResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Scope class wire form: <c>low | high | composite | undirected</c>. An
    /// unclassifiable rule comes back as <c>undirected</c> (not an error).
    /// </summary>
    [JsonPropertyName("scope_class")]
    public string? ScopeClass { get; init; }
}

/// <summary>Request envelope for <c>nocturne_alerts_replay</c>.</summary>
public sealed record RustReplayRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>The rule set, in any order.</summary>
    public required List<RustAlertRule> Rules { get; init; }

    /// <summary>The ticks, in time order.</summary>
    public required List<RustReplayTick> Ticks { get; init; }

    /// <summary>Whether <see cref="RustReplayResponse.Ticks"/> reports every evaluation instant and the rules evaluated there.</summary>
    [JsonPropertyName("include_ticks")]
    public bool IncludeTicks { get; init; }

    /// <summary>The end of the window: the rules are stepped on past the last tick up to, not including, it.</summary>
    public DateTime? Until { get; init; }
}

public sealed record RustReplayTick
{
    /// <summary>The tick instant, UTC.</summary>
    public required DateTime At { get; init; }

    /// <summary>
    /// SensorContext snapshot as of <see cref="At"/>, in the corpus <c>ScenarioContext</c> wire
    /// shape. Its <c>active_alerts</c> are replaced by the replay's own.
    /// </summary>
    public required JsonElement Context { get; init; }

    /// <summary>
    /// The rules a fire opening on this tick, or on a step before the next tick, is recorded for
    /// as suppressed by Do Not Disturb.
    /// </summary>
    [JsonPropertyName("suppressed_rule_ids")]
    public List<Guid>? SuppressedRuleIds { get; init; }

    /// <summary>
    /// A glucose reading arrived at <see cref="At"/>; otherwise the tick samples the other facts
    /// between readings.
    /// </summary>
    public bool Reading { get; init; } = true;
}

/// <summary>Response envelope for <c>nocturne_alerts_replay</c>.</summary>
public sealed record RustReplayResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>Rule ids in evaluation order.</summary>
    public List<Guid>? Order { get; init; }

    /// <summary>By instant, then evaluation order.</summary>
    public List<RustReplayEvent>? Events { get; init; }

    /// <summary>In evaluation order; a rule skipped on every tick has none.</summary>
    [JsonPropertyName("leaf_transitions")]
    public List<RustReplayLeafLog>? LeafTransitions { get; init; }

    /// <summary>Present exactly when the request set <see cref="RustReplayRequest.IncludeTicks"/>.</summary>
    public List<RustReplayTickOutcome>? Ticks { get; init; }
}

public sealed record RustReplayEvent(
    [property: JsonPropertyName("at"), JsonRequired] DateTime At,
    [property: JsonPropertyName("rule_id"), JsonRequired] Guid RuleId,
    [property: JsonPropertyName("kind"), JsonRequired] RustReplayEventKind Kind);

/// <summary>Event wire values in <see cref="RustReplayEvent.Kind"/>.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<RustReplayEventKind>))]
public enum RustReplayEventKind
{
    [JsonStringEnumMemberName("fired")] Fired,
    [JsonStringEnumMemberName("suppressed_by_dnd")] SuppressedByDnd,
    [JsonStringEnumMemberName("hysteresis_started")] HysteresisStarted,
    [JsonStringEnumMemberName("hysteresis_resumed")] HysteresisResumed,
    [JsonStringEnumMemberName("closed")] Closed,
    [JsonStringEnumMemberName("auto_resolved")] AutoResolved,
}

/// <summary>One rule's leaf log: per leaf, the first observation then every flip.</summary>
public sealed record RustReplayLeafLog(
    [property: JsonPropertyName("rule_id"), JsonRequired] Guid RuleId,
    [property: JsonPropertyName("leaves"), JsonRequired] List<RustReplayLeaf> Leaves);

public sealed record RustReplayLeaf(
    [property: JsonPropertyName("leaf_id"), JsonRequired] int LeafId,
    [property: JsonPropertyName("points"), JsonRequired] List<RustReplayLeafPoint> Points);

/// <summary>A leaf log point; <see cref="Value"/> is false when <see cref="Unknown"/>.</summary>
public sealed record RustReplayLeafPoint(
    [property: JsonPropertyName("at_ms"), JsonRequired] long AtMs,
    [property: JsonPropertyName("value"), JsonRequired] bool Value,
    [property: JsonPropertyName("unknown")] bool Unknown = false);

public sealed record RustReplayTickOutcome(
    [property: JsonPropertyName("at"), JsonRequired] DateTime At,
    [property: JsonPropertyName("rules"), JsonRequired] List<RustReplayRuleTick> Rules);

/// <summary>
/// A rule's state after one evaluation; <see cref="Met"/> and <see cref="Firing"/> are present
/// unless skipped. <see cref="Firing"/> is whether an excursion is open.
/// </summary>
public sealed record RustReplayRuleTick
{
    [JsonPropertyName("rule_id"), JsonRequired]
    public Guid RuleId { get; init; }

    public bool Skipped { get; init; }

    /// <summary>False when <see cref="Unknown"/>.</summary>
    public bool? Met { get; init; }

    /// <summary>The root is unknown; the wire carries it only when true.</summary>
    public bool Unknown { get; init; }

    public bool? Firing { get; init; }
}

/// <summary>
/// Request envelope for <c>nocturne_alerts_references_wall_clock</c> and
/// <c>nocturne_alerts_rule_reads</c>: a stored rule's body, auto-resolve configuration and
/// <c>on_unknown</c>.
/// </summary>
public sealed record RustRuleConditionsRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>Wire-format condition type discriminator (e.g. <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public required string ConditionType { get; init; }

    /// <summary>The kind-specific payload as stored in <c>alert_rules.condition_params</c>.</summary>
    [JsonPropertyName("condition_params")]
    public required JsonElement ConditionParams { get; init; }

    [JsonPropertyName("auto_resolve_enabled")]
    public bool AutoResolveEnabled { get; init; }

    /// <summary>The auto-resolve tree, a full condition node.</summary>
    [JsonPropertyName("auto_resolve_params")]
    public JsonElement? AutoResolveParams { get; init; }

    /// <summary>
    /// <c>hold</c>, <c>met</c> or <c>not_met</c>: a rule that reads an unknown root as known is
    /// wall-clock when its glucose conditions go stale (docs/alerts/engine-semantics.md §5.1).
    /// </summary>
    [JsonPropertyName("on_unknown")]
    public string OnUnknown { get; init; } = "hold";
}

/// <summary>Response envelope for <c>nocturne_alerts_references_wall_clock</c>.</summary>
public sealed record RustWallClockResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    [JsonPropertyName("references_wall_clock")]
    public bool? ReferencesWallClock { get; init; }
}

/// <summary>Response envelope for <c>nocturne_alerts_rule_reads</c>.</summary>
public sealed record RustRuleReadsResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>The context facts the rule reads, by wire name (docs/alerts/engine-semantics.md §5.2).</summary>
    public List<string>? Reads { get; init; }
}

/// <summary>Request envelope for <c>nocturne_alerts_describe</c>.</summary>
public sealed record RustDescribeRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>Wire-format condition type discriminator (e.g. <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public required string ConditionType { get; init; }

    /// <summary>The kind-specific payload as stored in <c>alert_rules.condition_params</c>.</summary>
    [JsonPropertyName("condition_params")]
    public required JsonElement ConditionParams { get; init; }

    /// <summary>
    /// An auto-resolve tree (a full condition node) to describe as well, or null for none.
    /// </summary>
    [JsonPropertyName("auto_resolve_params")]
    public JsonElement? AutoResolveParams { get; init; }
}

/// <summary>Response envelope for <c>nocturne_alerts_describe</c>.</summary>
public sealed record RustDescribeResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// The body's described tree (crates/nocturne-alerts-ffi/README.md, "Describe"); null when the
    /// body does not parse.
    /// </summary>
    public JsonElement? Tree { get; init; }

    /// <summary>
    /// The auto-resolve tree under the <c>auto_resolve</c> root; absent or null when the request
    /// carried none, or the tree cannot be evaluated.
    /// </summary>
    [JsonPropertyName("auto_resolve_tree")]
    public JsonElement? AutoResolveTree { get; init; }
}
