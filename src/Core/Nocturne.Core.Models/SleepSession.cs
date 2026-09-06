using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Represents a sleep session recorded by a wearable or health platform.
/// </summary>
/// <remarks>
/// <para><see cref="StartMills"/> and <see cref="EndMills"/> are computed properties that convert
/// from <see cref="StartTime"/> and <see cref="EndTime"/> to Unix milliseconds for v1/v3 API compatibility.</para>
/// </remarks>
public class SleepSession
{
    /// <summary>
    /// Gets or sets the unique identifier (UUID or original source ID).
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the UTC start time of the sleep session.
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the UTC end time of the sleep session.
    /// </summary>
    [JsonPropertyName("endTime")]
    public DateTime EndTime { get; set; }

    /// <summary>
    /// When the session started in Unix milliseconds (computed for v1/v3 compatibility).
    /// </summary>
    [JsonPropertyName("startMills")]
    public long StartMills => new DateTimeOffset(StartTime, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// When the session ended in Unix milliseconds (computed for v1/v3 compatibility).
    /// </summary>
    [JsonPropertyName("endMills")]
    public long EndMills => new DateTimeOffset(EndTime, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>
    /// Gets or sets the IANA timezone where the sleep was recorded.
    /// </summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    /// <summary>
    /// Gets or sets the session type classification.
    /// </summary>
    [JsonPropertyName("type")]
    public SleepSessionType Type { get; set; }

    /// <summary>
    /// Gets or sets how the session boundaries were detected.
    /// </summary>
    [JsonPropertyName("detectionMethod")]
    public SleepDetectionMethod DetectionMethod { get; set; }

    /// <summary>
    /// Gets or sets whether this is the user's primary overnight sleep.
    /// </summary>
    [JsonPropertyName("isMainSleep")]
    public bool? IsMainSleep { get; set; }

    /// <summary>
    /// Gets or sets the total duration of the session in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// Gets or sets the total time spent asleep in milliseconds.
    /// </summary>
    [JsonPropertyName("totalSleepMs")]
    public long TotalSleepMs { get; set; }

    /// <summary>
    /// Gets or sets the total time spent awake during the session in milliseconds.
    /// </summary>
    [JsonPropertyName("totalAwakeMs")]
    public long? TotalAwakeMs { get; set; }

    /// <summary>
    /// Gets or sets the total time in deep sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("deepSleepMs")]
    public long? DeepSleepMs { get; set; }

    /// <summary>
    /// Gets or sets the total time in light sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("lightSleepMs")]
    public long? LightSleepMs { get; set; }

    /// <summary>
    /// Gets or sets the total time in REM sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("remSleepMs")]
    public long? RemSleepMs { get; set; }

    /// <summary>
    /// Gets or sets the time from lights-out to sleep onset in milliseconds.
    /// </summary>
    [JsonPropertyName("sleepLatencyMs")]
    public long? SleepLatencyMs { get; set; }

    /// <summary>
    /// Gets or sets the sleep efficiency percentage (0-100).
    /// </summary>
    [JsonPropertyName("efficiency")]
    public float? Efficiency { get; set; }

    /// <summary>
    /// Gets or sets the number of restless periods during the session.
    /// </summary>
    [JsonPropertyName("restlessPeriods")]
    public int? RestlessPeriods { get; set; }

    /// <summary>
    /// Gets or sets the device-computed sleep quality score.
    /// </summary>
    [JsonPropertyName("sleepScore")]
    public short? SleepScore { get; set; }

    /// <summary>
    /// Gets or sets the average heart rate during sleep in BPM.
    /// </summary>
    [JsonPropertyName("avgHeartRate")]
    public float? AvgHeartRate { get; set; }

    /// <summary>
    /// Gets or sets the minimum heart rate during sleep in BPM.
    /// </summary>
    [JsonPropertyName("minHeartRate")]
    public float? MinHeartRate { get; set; }

    /// <summary>
    /// Gets or sets the average heart rate variability during sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("avgHrv")]
    public float? AvgHrv { get; set; }

    /// <summary>
    /// Gets or sets the average breathing rate during sleep in breaths per minute.
    /// </summary>
    [JsonPropertyName("avgBreathRate")]
    public float? AvgBreathRate { get; set; }

    /// <summary>
    /// Gets or sets the average blood oxygen saturation during sleep.
    /// </summary>
    [JsonPropertyName("avgSpo2")]
    public float? AvgSpo2 { get; set; }

    /// <summary>
    /// Gets or sets the origin platform that recorded the sleep data.
    /// </summary>
    [JsonPropertyName("source")]
    public SleepSource Source { get; set; }

    /// <summary>
    /// Gets or sets the name of the device that recorded the session.
    /// </summary>
    [JsonPropertyName("sourceDevice")]
    public string? SourceDevice { get; set; }

    /// <summary>
    /// Gets or sets the name of the application that submitted the data.
    /// </summary>
    [JsonPropertyName("sourceApp")]
    public string? SourceApp { get; set; }

    /// <summary>
    /// Gets or sets the original ID from the source system for deduplication.
    /// </summary>
    [JsonPropertyName("originalId")]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Gets or sets source-specific metadata (stored as JSON).
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Gets or sets the created-at timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the updated-at timestamp.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the sleep stage intervals within this session.
    /// </summary>
    [JsonPropertyName("stages")]
    public List<SleepStageInterval>? Stages { get; set; }

    /// <summary>
    /// Gets or sets biometric samples collected during this session.
    /// </summary>
    [JsonPropertyName("biometricSamples")]
    public List<SleepBiometricSample>? BiometricSamples { get; set; }
}
