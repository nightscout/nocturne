using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Represents battery statistics for a device over a time period
/// </summary>
public class BatteryStatistics
{
    /// <summary>
    /// Gets or sets the device identifier/name
    /// </summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a friendly display name for the device
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the statistics period (mills)
    /// </summary>
    [JsonPropertyName("periodStartMills")]
    public long PeriodStartMills { get; set; }

    /// <summary>
    /// Gets or sets the end of the statistics period (mills)
    /// </summary>
    [JsonPropertyName("periodEndMills")]
    public long PeriodEndMills { get; set; }

    /// <summary>
    /// Gets or sets the total number of readings in the period
    /// </summary>
    [JsonPropertyName("readingCount")]
    public int ReadingCount { get; set; }

    /// <summary>
    /// Gets or sets the current/most recent battery level
    /// </summary>
    [JsonPropertyName("currentLevel")]
    public int? CurrentLevel { get; set; }

    /// <summary>
    /// Gets or sets whether the device is currently charging
    /// </summary>
    [JsonPropertyName("isCharging")]
    public bool IsCharging { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last reading (mills)
    /// </summary>
    [JsonPropertyName("lastReadingMills")]
    public long? LastReadingMills { get; set; }

    // ==========================================
    // Basic Statistics
    // ==========================================

    /// <summary>
    /// Gets or sets the average battery level over the period
    /// </summary>
    [JsonPropertyName("averageLevel")]
    public double? AverageLevel { get; set; }

    /// <summary>
    /// Gets or sets the minimum battery level observed
    /// </summary>
    [JsonPropertyName("minLevel")]
    public int? MinLevel { get; set; }

    /// <summary>
    /// Gets or sets the maximum battery level observed
    /// </summary>
    [JsonPropertyName("maxLevel")]
    public int? MaxLevel { get; set; }

    // ==========================================
    // Charge Cycle Statistics
    // ==========================================

    /// <summary>
    /// Gets or sets the number of complete charge cycles in the period
    /// </summary>
    [JsonPropertyName("chargeCycleCount")]
    public int ChargeCycleCount { get; set; }

    /// <summary>
    /// Gets or sets the average charge duration in minutes
    /// </summary>
    [JsonPropertyName("averageChargeDurationMinutes")]
    public double? AverageChargeDurationMinutes { get; set; }

    /// <summary>
    /// Gets or sets the average time between charges (discharge duration) in minutes
    /// </summary>
    [JsonPropertyName("averageDischargeDurationMinutes")]
    public double? AverageDischargeDurationMinutes { get; set; }

    /// <summary>
    /// Gets or sets the average time between charges in hours (more user-friendly)
    /// </summary>
    [JsonPropertyName("averageTimeBetweenChargesHours")]
    public double? AverageTimeBetweenChargesHours =>
        AverageDischargeDurationMinutes.HasValue
            ? AverageDischargeDurationMinutes.Value / 60.0
            : null;

    /// <summary>
    /// Gets or sets the longest discharge duration observed (minutes)
    /// </summary>
    [JsonPropertyName("longestDischargeDurationMinutes")]
    public double? LongestDischargeDurationMinutes { get; set; }

    /// <summary>
    /// Gets or sets the shortest discharge duration observed (minutes)
    /// </summary>
    [JsonPropertyName("shortestDischargeDurationMinutes")]
    public double? ShortestDischargeDurationMinutes { get; set; }

    // ==========================================
    // Time in Range Statistics
    // ==========================================

    /// <summary>
    /// Gets or sets the percentage of time battery was above 80%
    /// </summary>
    [JsonPropertyName("timeAbove80Percent")]
    public double TimeAbove80Percent { get; set; }

    /// <summary>
    /// Gets or sets the percentage of time battery was between 30-80%
    /// </summary>
    [JsonPropertyName("timeBetween30And80Percent")]
    public double TimeBetween30And80Percent { get; set; }

    /// <summary>
    /// Gets or sets the percentage of time battery was below 30% (warning zone)
    /// </summary>
    [JsonPropertyName("timeBelow30Percent")]
    public double TimeBelow30Percent { get; set; }

    /// <summary>
    /// Gets or sets the percentage of time battery was below 20% (urgent zone)
    /// </summary>
    [JsonPropertyName("timeBelow20Percent")]
    public double TimeBelow20Percent { get; set; }

    // ==========================================
    // Warning/Alert Statistics
    // ==========================================

    /// <summary>
    /// Gets or sets the number of times battery dropped below warning threshold (30%)
    /// </summary>
    [JsonPropertyName("warningEventCount")]
    public int WarningEventCount { get; set; }

    /// <summary>
    /// Gets or sets the number of times battery dropped below urgent threshold (20%)
    /// </summary>
    [JsonPropertyName("urgentEventCount")]
    public int UrgentEventCount { get; set; }

    // ==========================================
    // Display/UI Helpers
    // ==========================================

    /// <summary>
    /// Gets the current battery display string
    /// </summary>
    [JsonPropertyName("display")]
    public string Display =>
        CurrentLevel.HasValue ? $"{CurrentLevel}%{(IsCharging ? "⚡" : "")}" : "?%";

    /// <summary>
    /// Gets the battery level category for icon display (25, 50, 75, 100)
    /// </summary>
    [JsonPropertyName("level")]
    public int Level =>
        CurrentLevel switch
        {
            >= 95 => 100,
            >= 55 => 75,
            >= 30 => 50,
            >= 1 => 25,
            _ => 0,
        };

    /// <summary>
    /// Gets the notification status based on current level
    /// </summary>
    [JsonPropertyName("status")]
    public string Status =>
        CurrentLevel switch
        {
            <= 20 => "urgent",
            <= 30 => "warn",
            _ => "ok",
        };
}
