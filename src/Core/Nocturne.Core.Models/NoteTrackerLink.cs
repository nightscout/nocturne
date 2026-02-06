using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a link between a note and a tracker definition with notification thresholds
/// </summary>
public class NoteTrackerLink
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the tracker definition identifier
    /// </summary>
    [JsonPropertyName("trackerDefinitionId")]
    public Guid TrackerDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the notification thresholds for this tracker link
    /// </summary>
    [JsonPropertyName("thresholds")]
    public List<NoteTrackerThreshold> Thresholds { get; set; } = new();

    /// <summary>
    /// Gets or sets when this link was created
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}
