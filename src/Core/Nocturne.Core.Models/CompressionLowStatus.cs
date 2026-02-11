using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Status of a compression low suggestion
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CompressionLowStatus>))]
public enum CompressionLowStatus
{
    /// <summary>
    /// Suggestion is pending user review
    /// </summary>
    Pending,

    /// <summary>
    /// User accepted the suggestion, StateSpan created
    /// </summary>
    Accepted,

    /// <summary>
    /// User dismissed the suggestion
    /// </summary>
    Dismissed
}
