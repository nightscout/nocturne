using System.Text.Json.Serialization;

namespace Nocturne.Alerts.Corpus;

// ---------------------------------------------------------------------------
// Replay scenario input (AlertEngineCorpus/replay/<name>.json)
// ---------------------------------------------------------------------------

/// <summary>
/// A replay scenario (docs/alerts/engine-semantics.md §8): a rule set and the ticks the replay
/// driver evaluates it over. Serialised with <see cref="CorpusJson.Options"/>.
/// </summary>
public sealed record ReplayScenarioFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required List<ScenarioRule> Rules { get; init; }

    public required List<ReplayScenarioTick> Ticks { get; init; }
}

public sealed record ReplayScenarioTick
{
    public required DateTime At { get; init; }

    /// <summary>The context as of <see cref="At"/>; replay supplies its own active alerts.</summary>
    public required ScenarioContext Context { get; init; }

    /// <summary>Rules a fire opening on this tick is recorded for as suppressed by Do Not Disturb.</summary>
    [JsonPropertyName("suppressed_rule_ids")]
    public List<Guid>? SuppressedRuleIds { get; init; }

    /// <summary>A reading arrived at <see cref="At"/>; false is a sample between readings.</summary>
    public bool Reading { get; init; } = true;
}

// ---------------------------------------------------------------------------
// Replay expected snapshot (AlertEngineCorpus/replay/<name>.expected.json)
// ---------------------------------------------------------------------------

public sealed record ReplayExpectedFile
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    public required string Scenario { get; init; }

    /// <summary>Rule ids in evaluation order.</summary>
    public required List<Guid> Order { get; init; }

    public required List<ReplayExpectedEvent> Events { get; init; }

    [JsonPropertyName("leaf_transitions")]
    public required List<ReplayExpectedLeafLog> LeafTransitions { get; init; }

    public required List<ReplayExpectedTick> Ticks { get; init; }
}

/// <summary>
/// <see cref="Kind"/> is <c>fired | suppressed_by_dnd | hysteresis_started | hysteresis_resumed |
/// closed | auto_resolved</c>.
/// </summary>
public sealed record ReplayExpectedEvent(
    [property: JsonPropertyName("at"), JsonConverter(typeof(CorpusJson.TickInstantConverter))] DateTime At,
    [property: JsonPropertyName("rule_id")] Guid RuleId,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record ReplayExpectedLeafLog(
    [property: JsonPropertyName("rule_id")] Guid RuleId,
    [property: JsonPropertyName("leaves")] List<ReplayExpectedLeaf> Leaves);

public sealed record ReplayExpectedLeaf(
    [property: JsonPropertyName("leaf_id")] int LeafId,
    [property: JsonPropertyName("points")] List<ReplayExpectedPoint> Points);

/// <summary><see cref="Value"/> is false when <see cref="Unknown"/>, which is carried only when true.</summary>
public sealed record ReplayExpectedPoint(
    [property: JsonPropertyName("at_ms")] long AtMs,
    [property: JsonPropertyName("value")] bool Value,
    [property: JsonPropertyName("unknown")] bool? Unknown = null);

/// <summary>An evaluation instant: a tick, or a step between ticks on the engine's 100 ns grid.</summary>
public sealed record ReplayExpectedTick(
    [property: JsonPropertyName("at"), JsonConverter(typeof(CorpusJson.TickInstantConverter))] DateTime At,
    [property: JsonPropertyName("rules")] List<ReplayExpectedRuleTick> Rules);

/// <summary>A skipped rule carries only <see cref="Skipped"/>; any other carries <see cref="Met"/> and <see cref="Firing"/>.</summary>
public sealed record ReplayExpectedRuleTick
{
    [JsonPropertyName("rule_id")]
    public required Guid RuleId { get; init; }

    public bool? Skipped { get; init; }

    public bool? Met { get; init; }

    /// <summary>The root is unknown; carried only when true.</summary>
    public bool? Unknown { get; init; }

    public bool? Firing { get; init; }
}
