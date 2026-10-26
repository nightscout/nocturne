using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Battery;

/// <summary>
/// Battery statistics for a device over a time period.
/// Aggregates <see cref="BatteryReading"/>s and <see cref="ChargeCycle"/>s into summary metrics.
/// </summary>
/// <seealso cref="BatteryReading"/>
/// <seealso cref="ChargeCycle"/>
/// <seealso cref="CurrentBatteryStatus"/>
public class BatteryStatistics
{
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Friendly display name for the device
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Start of the statistics period (mills)
    /// </summary>
    [JsonPropertyName("periodStartMills")]
    public long PeriodStartMills { get; set; }

    /// <summary>
    /// End of the statistics period (mills)
    /// </summary>
    [JsonPropertyName("periodEndMills")]
    public long PeriodEndMills { get; set; }

    /// <summary>
    /// Total number of readings in the period
    /// </summary>
    [JsonPropertyName("readingCount")]
    public int ReadingCount { get; set; }

    /// <summary>
    /// Current/most recent battery level
    /// </summary>
    [JsonPropertyName("currentLevel")]
    public int? CurrentLevel { get; set; }

    /// <summary>
    /// Whether the device is currently charging
    /// </summary>
    [JsonPropertyName("isCharging")]
    public bool IsCharging { get; set; }

    /// <summary>
    /// Timestamp of the last reading (mills)
    /// </summary>
    [JsonPropertyName("lastReadingMills")]
    public long? LastReadingMills { get; set; }

    // ==========================================
    // Basic Statistics
    // ==========================================

    /// <summary>
    /// Average battery level over the period
    /// </summary>
    [JsonPropertyName("averageLevel")]
    public double? AverageLevel { get; set; }

    /// <summary>
    /// Minimum battery level observed
    /// </summary>
    [JsonPropertyName("minLevel")]
    public int? MinLevel { get; set; }

    /// <summary>
    /// Maximum battery level observed
    /// </summary>
    [JsonPropertyName("maxLevel")]
    public int? MaxLevel { get; set; }

    // ==========================================
    // Charge Cycle Statistics
    // ==========================================

    /// <summary>
    /// Number of complete charge cycles in the period
    /// </summary>
    [JsonPropertyName("chargeCycleCount")]
    public int ChargeCycleCount { get; set; }

    /// <summary>
    /// Average charge duration in minutes
    /// </summary>
    [JsonPropertyName("averageChargeDurationMinutes")]
    public double? AverageChargeDurationMinutes { get; set; }

    /// <summary>
    /// Average time between charges (discharge duration) in minutes
    /// </summary>
    [JsonPropertyName("averageDischargeDurationMinutes")]
    public double? AverageDischargeDurationMinutes { get; set; }

    /// <summary>
    /// Average time between charges in hours (more user-friendly)
    /// </summary>
    [JsonPropertyName("averageTimeBetweenChargesHours")]
    public double? AverageTimeBetweenChargesHours =>
        AverageDischargeDurationMinutes.HasValue
            ? AverageDischargeDurationMinutes.Value / 60.0
            : null;

    /// <summary>
    /// Longest discharge duration observed (minutes)
    /// </summary>
    [JsonPropertyName("longestDischargeDurationMinutes")]
    public double? LongestDischargeDurationMinutes { get; set; }

    /// <summary>
    /// Shortest discharge duration observed (minutes)
    /// </summary>
    [JsonPropertyName("shortestDischargeDurationMinutes")]
    public double? ShortestDischargeDurationMinutes { get; set; }

    // ==========================================
    // Time in Range Statistics
    // ==========================================

    /// <summary>
    /// Percentage of time battery was above 80%
    /// </summary>
    [JsonPropertyName("timeAbove80Percent")]
    public double TimeAbove80Percent { get; set; }

    /// <summary>
    /// Percentage of time battery was between 30-80%
    /// </summary>
    [JsonPropertyName("timeBetween30And80Percent")]
    public double TimeBetween30And80Percent { get; set; }

    /// <summary>
    /// Percentage of time battery was below 30% (warning zone)
    /// </summary>
    [JsonPropertyName("timeBelow30Percent")]
    public double TimeBelow30Percent { get; set; }

    /// <summary>
    /// Percentage of time battery was below 20% (urgent zone)
    /// </summary>
    [JsonPropertyName("timeBelow20Percent")]
    public double TimeBelow20Percent { get; set; }

    // ==========================================
    // Warning/Alert Statistics
    // ==========================================

    /// <summary>
    /// Number of times battery dropped below warning threshold (30%)
    /// </summary>
    [JsonPropertyName("warningEventCount")]
    public int WarningEventCount { get; set; }

    /// <summary>
    /// Number of times battery dropped below urgent threshold (20%)
    /// </summary>
    [JsonPropertyName("urgentEventCount")]
    public int UrgentEventCount { get; set; }

    // ==========================================
    // Display/UI Helpers
    // ==========================================

    [JsonPropertyName("display")]
    public string Display =>
        CurrentLevel.HasValue ? $"{CurrentLevel}%{(IsCharging ? "⚡" : "")}" : "?%";

    /// <summary>
    /// Battery level category for icon display (0, 25, 50, 75, 100)
    /// </summary>
    [JsonPropertyName("level")]
    public int Level => BatteryLevelBucket.Of(CurrentLevel);

    /// <summary>
    /// Notification status based on current level
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
