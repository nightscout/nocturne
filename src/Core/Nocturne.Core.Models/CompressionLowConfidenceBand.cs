using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// How far a <see cref="CompressionLowSuggestion"/> clears the detection threshold.
/// </summary>
/// <seealso cref="CompressionLowConfidence"/>
[JsonConverter(typeof(JsonStringEnumConverter<CompressionLowConfidenceBand>))]
public enum CompressionLowConfidenceBand
{
    /// <summary>
    /// Confidence from <see cref="CompressionLowConfidence.High"/> up.
    /// </summary>
    High,

    /// <summary>
    /// Confidence from <see cref="CompressionLowConfidence.MinThreshold"/> up to the High boundary.
    /// A stored score below the threshold, from before a rescale, is Medium too.
    /// </summary>
    Medium
}
