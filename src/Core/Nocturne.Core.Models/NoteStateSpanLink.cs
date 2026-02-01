using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a link between a note and a state span
/// </summary>
public class NoteStateSpanLink
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the state span identifier
    /// </summary>
    [JsonPropertyName("stateSpanId")]
    public string StateSpanId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this link was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}
