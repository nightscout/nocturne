using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a notification threshold for a note tracker link
/// </summary>
public class NoteTrackerThreshold
{
    /// <summary>
    /// Gets or sets the unique identifier
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the hours offset from tracker start (positive) or end (negative)
    /// </summary>
    [JsonPropertyName("hoursOffset")]
    public decimal HoursOffset { get; set; }

    /// <summary>
    /// Gets or sets the notification urgency level
    /// </summary>
    [JsonPropertyName("urgency")]
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Gets or sets the optional description for this threshold
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
