using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// Coarse glucose bucket derived from the active <see cref="V4.TargetRangeEntry"/> boundaries.
/// Buckets are mutually exclusive — a single glucose reading falls into exactly one. Boundaries
/// follow the clinical convention: <c>54</c> is Low (not VeryLow), <c>70</c> is TightRange,
/// <c>140</c> is TightRange, <c>141</c> is InRange, <c>180</c> is InRange, <c>181</c> is High,
/// <c>250</c> is High, <c>251</c> is VeryHigh.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseBucket>))]
public enum GlucoseBucket
{
    /// <summary>Glucose below the VeryLow threshold (default <c>&lt;54</c> mg/dL).</summary>
    [JsonStringEnumMemberName("very_low")] VeryLow,

    /// <summary>Glucose in the Low band (default <c>[54, 70)</c> mg/dL).</summary>
    [JsonStringEnumMemberName("low")] Low,

    /// <summary>Glucose inside the tight clinical range (default <c>[70, 140]</c> mg/dL).</summary>
    [JsonStringEnumMemberName("tight_range")] TightRange,

    /// <summary>Glucose inside the standard target range (default <c>(140, 180]</c> mg/dL).</summary>
    [JsonStringEnumMemberName("in_range")] InRange,

    /// <summary>Glucose in the High band (default <c>(180, 250]</c> mg/dL).</summary>
    [JsonStringEnumMemberName("high")] High,

    /// <summary>Glucose above the VeryHigh threshold (default <c>&gt;250</c> mg/dL).</summary>
    [JsonStringEnumMemberName("very_high")] VeryHigh,
}
