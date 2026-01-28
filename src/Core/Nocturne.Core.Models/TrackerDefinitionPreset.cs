using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// A preset template for a common tracker definition (e.g., CGM sensors).
/// Used to pre-populate the create form when adding a new tracker definition.
/// </summary>
public class TrackerDefinitionPreset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("category")]
    public TrackerCategory Category { get; set; }

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "activity";

    [JsonPropertyName("lifespanHours")]
    public int? LifespanHours { get; set; }

    /// <summary>
    /// Returns the full catalog of common tracker definition presets.
    /// </summary>
    public static List<TrackerDefinitionPreset> GetAll() =>
    [
        // === CGM Sensors ===
        new() { Name = "Dexcom G6", Description = "Dexcom G6 CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 240 },
        new() { Name = "Dexcom G7", Description = "Dexcom G7 CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 240 },
        new() { Name = "Dexcom One+", Description = "Dexcom One+ CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 240 },
        new() { Name = "Dexcom 15 Day", Description = "Dexcom Stelo / 15-day CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 360 },
        new() { Name = "Freestyle Libre 2", Description = "Abbott Freestyle Libre 2 CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 336 },
        new() { Name = "Freestyle Libre 2+", Description = "Abbott Freestyle Libre 2+ CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 336 },
        new() { Name = "Freestyle Libre 3", Description = "Abbott Freestyle Libre 3 CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 336 },
        new() { Name = "Freestyle Libre 3+", Description = "Abbott Freestyle Libre 3+ CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 336 },
        new() { Name = "Medtronic Guardian", Description = "Medtronic Guardian CGM sensor", Category = TrackerCategory.Sensor, Icon = "activity", LifespanHours = 168 },
    ];
}
