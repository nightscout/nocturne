using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Widget;

/// <summary>
/// Current status of a tracker for widget display.
/// Includes identification, timing, and urgency information.
/// </summary>
public class V4TrackerStatus
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Identifier of the tracker definition this instance is based on.
    /// </summary>
    [JsonPropertyName("definitionId")]
    public Guid DefinitionId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Category of the tracker for grouping and filtering.
    /// </summary>
    [JsonPropertyName("category")]
    public TrackerCategory Category { get; set; }

    /// <summary>
    /// Mode of tracker operation (Duration or Event).
    /// </summary>
    [JsonPropertyName("mode")]
    public TrackerMode Mode { get; set; }

    /// <summary>
    /// Age of the tracker in hours since it was started.
    /// Only applicable for Duration mode trackers.
    /// </summary>
    [JsonPropertyName("ageHours")]
    public double? AgeHours { get; set; }

    /// <summary>
    /// Hours until the scheduled event occurs.
    /// Only applicable for Event mode trackers.
    /// </summary>
    [JsonPropertyName("hoursUntilEvent")]
    public double? HoursUntilEvent { get; set; }

    /// <summary>
    /// Current notification urgency level based on thresholds.
    /// </summary>
    [JsonPropertyName("urgency")]
    public NotificationUrgency Urgency { get; set; }

    /// <summary>
    /// Configured lifespan in hours for Duration mode trackers.
    /// </summary>
    [JsonPropertyName("lifespanHours")]
    public int? LifespanHours { get; set; }

    /// <summary>
    /// Percentage of lifespan elapsed (0-100).
    /// Calculated as (AgeHours / LifespanHours) * 100 for Duration mode trackers.
    /// </summary>
    [JsonPropertyName("percentElapsed")]
    public double? PercentElapsed { get; set; }
}
