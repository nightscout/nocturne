using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Alerts;

/// <summary>
/// The end of a window's readings a <c>glucose_extreme</c> condition reads
/// (docs/alerts/engine-semantics.md §3, windows).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AlertWindowExtreme>))]
public enum AlertWindowExtreme
{
    /// <summary>The lowest reading in the window.</summary>
    [JsonStringEnumMemberName("min")] Min,

    /// <summary>The highest reading in the window.</summary>
    [JsonStringEnumMemberName("max")] Max,
}
