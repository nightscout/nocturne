using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The fact a glucose comparison's limit names in place of a number
/// (docs/alerts/engine-semantics.md §3, a limit from a fact).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertLimitFact>))]
public enum AlertLimitFact
{
    /// <summary>The newest reading, read as a <c>threshold</c> reads it.</summary>
    [JsonStringEnumMemberName("latest_glucose")] LatestGlucose,

    /// <summary>The low end of the profile's target range in effect at the evaluation instant.</summary>
    [JsonStringEnumMemberName("target_low")] TargetLow,

    /// <summary>The high end of the same range.</summary>
    [JsonStringEnumMemberName("target_high")] TargetHigh,
}

/// <summary>
/// A limit read from a fact: the fact's value plus <paramref name="Offset"/>, in mg/dL.
/// </summary>
/// <param name="Fact">The fact whose value is the limit.</param>
/// <param name="Offset">Added to the fact's value; absent is 0. At most 1000 either side of 0.</param>
public sealed record AlertLimitReference(
    [property: JsonPropertyName("fact")] AlertLimitFact Fact,
    [property: JsonPropertyName("offset")] decimal? Offset = null);
