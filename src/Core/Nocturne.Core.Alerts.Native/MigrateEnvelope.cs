using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Alerts.Native;

/// <summary>
/// Request envelope for <c>nocturne_alerts_migrate</c>: one stored condition tree to rewrite from
/// condition schema 1 to schema 2 (docs/alerts/engine-semantics.md §1.5). Exactly one of
/// <see cref="ConditionType"/> and <see cref="Root"/> is set; build it with <see cref="Body"/> or
/// <see cref="Node"/>.
/// </summary>
public sealed record RustMigrateRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>A payload-only rule body when <see cref="ConditionType"/> is set, else a full node.</summary>
    [JsonPropertyName("tree")]
    public required JsonElement Tree { get; init; }

    /// <summary>Wire-format kind a rule body is the payload of (e.g. <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public string? ConditionType { get; init; }

    /// <summary>Root path segment of a full node (<c>auto_resolve</c>, <c>snooze</c>).</summary>
    [JsonPropertyName("root")]
    public string? Root { get; init; }

    /// <summary>A rule body, <c>alert_rules.condition_params</c>, stored for <paramref name="conditionType"/>.</summary>
    public static RustMigrateRequest Body(string conditionType, JsonElement tree) =>
        new() { Tree = tree, ConditionType = conditionType };

    /// <summary>A full condition node evaluated under <paramref name="root"/>.</summary>
    public static RustMigrateRequest Node(string root, JsonElement tree) =>
        new() { Tree = tree, Root = root };
}

/// <summary>What <c>nocturne_alerts_migrate</c> did with a tree.</summary>
[JsonConverter(typeof(StrictStringEnumConverter<RustMigrateOutcome>))]
public enum RustMigrateOutcome
{
    /// <summary>The tree already names schema 2; there is nothing to store.</summary>
    [JsonStringEnumMemberName("current")] Current,

    /// <summary><see cref="RustMigrateResponse.Tree"/> is the tree in schema 2.</summary>
    [JsonStringEnumMemberName("migrated")] Migrated,

    /// <summary>
    /// The tree cannot be read as schema 1 (<see cref="RustMigrateResponse.Issue"/>). It is kept as
    /// it is and reads as <c>unsupported_schema</c>.
    /// </summary>
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>Response envelope for <c>nocturne_alerts_migrate</c>.</summary>
public sealed record RustMigrateResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    public RustMigrateOutcome? Outcome { get; init; }

    /// <summary>The schema 2 tree, when <see cref="Outcome"/> is <see cref="RustMigrateOutcome.Migrated"/>.</summary>
    public JsonElement? Tree { get; init; }

    /// <summary>
    /// Whether a <c>site_age</c> or <c>sensor_age</c> bound was not a whole number of minutes and
    /// was rounded toward the side that fires no later; the rule is then due for review.
    /// </summary>
    public bool Rounded { get; init; }

    /// <summary>Why the tree could not be migrated, when <see cref="Outcome"/> is <see cref="RustMigrateOutcome.Failed"/>.</summary>
    public RustMigrateIssue? Issue { get; init; }
}

/// <summary>
/// Where a schema 1 tree could not be read. Like <see cref="RustValidationIssue"/>, it never
/// carries a payload value.
/// </summary>
/// <param name="Path">Condition path of the node the migration stopped at.</param>
/// <param name="Reason">Stable reason code (docs/alerts/engine-semantics.md §1.4).</param>
/// <param name="Field">The payload field the problem is on, when there is one.</param>
public sealed record RustMigrateIssue(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("field")] string? Field);
