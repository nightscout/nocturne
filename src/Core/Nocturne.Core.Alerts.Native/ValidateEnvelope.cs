using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Alerts.Native;

/// <summary>
/// Request envelope for <c>nocturne_alerts_validate</c>: the condition trees of a rule being
/// saved, each in its stored shape.
/// </summary>
public sealed record RustValidateRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = AlertEnvelopeJson.SchemaVersion;

    /// <summary>Wire-format condition type discriminator (e.g. <c>"threshold"</c>).</summary>
    [JsonPropertyName("condition_type")]
    public required string ConditionType { get; init; }

    /// <summary>The payload-only <c>alert_rules.condition_params</c> object.</summary>
    [JsonPropertyName("condition_params")]
    public required JsonElement ConditionParams { get; init; }

    /// <summary>The full auto-resolve ConditionNode, or null to skip it.</summary>
    [JsonPropertyName("auto_resolve_params")]
    public JsonElement? AutoResolveParams { get; init; }

    /// <summary><c>client_configuration.snooze.conditions</c>, or null to skip them.</summary>
    [JsonPropertyName("snooze_conditions")]
    public JsonElement? SnoozeConditions { get; init; }
}

/// <summary>Response envelope for <c>nocturne_alerts_validate</c>.</summary>
public sealed record RustValidateResponse : IRustResponseEnvelope
{
    [JsonPropertyName("schema_version"), JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public bool Ok { get; init; }

    /// <summary>Error message when <see cref="Ok"/> is false.</summary>
    public string? Error { get; init; }

    public bool Valid { get; init; }

    public List<RustValidationIssue>? Issues { get; init; }
}

/// <summary>
/// One problem a rule save rejects (docs/alerts/engine-semantics.md §1.4).
/// </summary>
/// <param name="Scope"><c>condition</c>, <c>auto_resolve</c> or <c>snooze</c>.</param>
/// <param name="Path">Condition path of the offending node.</param>
/// <param name="Reason">Stable reason code; never carries a payload value.</param>
/// <param name="Field">The payload field the problem is on, when there is one.</param>
public sealed record RustValidationIssue(
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("field")] string? Field);
