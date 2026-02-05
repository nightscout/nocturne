using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Represents the current status of a tracker for widget display.
/// Includes identification, timing, and urgency information.
/// </summary>
public class V4TrackerStatus
{
    /// <summary>
    /// Gets or sets the unique identifier for this tracker instance.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the tracker definition this instance is based on.
    /// </summary>
    [JsonPropertyName("definitionId")]
    public Guid DefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the display name of the tracker.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the icon identifier for the tracker.
    /// </summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category of the tracker for grouping and filtering.
    /// </summary>
    [JsonPropertyName("category")]
    public TrackerCategory Category { get; set; }

    /// <summary>
    /// Gets or sets the mode of tracker operation (Duration or Event).
    /// </summary>
    [JsonPropertyName("mode")]
    public TrackerMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the age of the tracker in hours since it was started.
    /// Only applicable for Duration mode trackers.
    /// </summary>
    [JsonPropertyName("ageHours")]
    public double? AgeHours { get; set; }

    /// <summary>
    /// Gets or sets the hours until the scheduled event occurs.
    /// Only applicable for Event mode trackers.
    /// </summary>
    [JsonPropertyName("hoursUntilEvent")]
    public double? HoursUntilEvent { get; set; }

    /// <summary>
    /// Gets or sets the current notification urgency level based on thresholds.
    /// </summary>
    [JsonPropertyName("urgency")]
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Gets or sets the configured lifespan in hours for Duration mode trackers.
    /// </summary>
    [JsonPropertyName("lifespanHours")]
    public int? LifespanHours { get; set; }

    /// <summary>
    /// Gets or sets the percentage of lifespan elapsed (0-100).
    /// Calculated as (AgeHours / LifespanHours) * 100 for Duration mode trackers.
    /// </summary>
    [JsonPropertyName("percentElapsed")]
    public double? PercentElapsed { get; set; }
}
