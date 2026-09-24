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

/// <summary>
/// Persisted tracker state carried in and out of every evaluate call. The
/// per-rule fields (<see cref="State"/>, <see cref="ConfirmationCount"/>,
/// <see cref="ActiveExcursionOrdinal"/>, <see cref="UpdatedAt"/>) are absent
/// until the rule has been evaluated at least once;
/// <see cref="NextExcursionOrdinal"/> is shared across all rules of a tenant
/// and must be threaded between calls in evaluation order.
/// </summary>
public sealed record RustTrackerState
{
    /// <summary><c>idle | confirming | active | hysteresis</c>, or null when no state exists yet.</summary>
    public string? State { get; init; }

    [JsonPropertyName("confirmation_count")]
    public int ConfirmationCount { get; init; }

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

    /// <summary>1-based ordinal the next opened excursion will receive.</summary>
    [JsonPropertyName("next_excursion_ordinal")]
    public int NextExcursionOrdinal { get; init; } = 1;
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

    /// <summary>Persisted sustained-timer state for this rule: condition path → first-true instant.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

    public RustTrackerState? Tracker { get; init; }

    /// <summary>
    /// Whether <c>result.leaves</c> is produced. Every leaf is otherwise evaluated alone on every
    /// call, so a caller that does not read the leaves sends false.
    /// </summary>
    [JsonPropertyName("include_leaves")]
    public bool IncludeLeaves { get; init; } = true;
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

    /// <summary>Post-evaluation timer state to persist for this rule.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

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

    /// <summary>Root condition truth; present on every result that was not skipped.</summary>
    public bool? Root { get; init; }

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

    /// <summary>The auto-resolve pass force-closed the excursion; the wire carries it only when true.</summary>
    [JsonPropertyName("auto_resolved")]
    public bool AutoResolved { get; init; }

    /// <summary>Sustained-timer mutations performed during the call, in execution order; omitted when none.</summary>
    [JsonPropertyName("timer_ops")]
    public List<RustTimerOp>? TimerOps { get; init; }
}

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
}

/// <summary>
/// Reads only the declared member names; an integer or an unknown name is a
/// <see cref="JsonException"/>, so a value this binding does not know never maps to a default.
/// </summary>
public sealed class StrictStringEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(null, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>One per-leaf force-eval truth in <see cref="RustRuleResult.Leaves"/>.</summary>
public sealed record RustLeafValue(
    [property: JsonPropertyName("leaf_id")] int LeafId,
    [property: JsonPropertyName("value")] bool Value);

/// <summary>One sustained-timer mutation: <c>op</c> is <c>set</c> (with <c>at</c>) or <c>clear</c>.</summary>
public sealed record RustTimerOp(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("at")] DateTime? At);

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

    /// <summary>Persisted sustained-timer state for the rule: condition path → first-true instant.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }
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

    /// <summary>The node's truth; present on every successful response.</summary>
    public bool? Value { get; init; }

    /// <summary>Post-evaluation timer state to persist for the rule.</summary>
    public Dictionary<string, DateTime>? Timers { get; init; }

    /// <summary>Sustained-timer mutations performed during the call, in execution order.</summary>
    [JsonPropertyName("timer_ops")]
    public List<RustTimerOp>? TimerOps { get; init; }
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

    /// <summary>Leaf-id → path pairs (LeafIdentity pre-order ids).</summary>
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
