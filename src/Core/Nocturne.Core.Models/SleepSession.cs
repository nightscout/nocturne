using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Sleep session recorded by a wearable or health platform.
/// </summary>
/// <remarks>
/// <para><see cref="StartMills"/> and <see cref="EndMills"/> are computed properties that convert
/// from <see cref="StartTime"/> and <see cref="EndTime"/> to Unix milliseconds for v1/v3 API compatibility.</para>
/// </remarks>
public class SleepSession
{
    /// <summary>
    /// Unique identifier (UUID or original source ID).
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// UTC start time of the sleep session.
    /// </summary>
    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// UTC end time of the sleep session.
    /// </summary>
    [JsonPropertyName("endTime")]
    public DateTime EndTime { get; set; }

    /// <summary>
    /// When the session started in Unix milliseconds (computed for v1/v3 compatibility).
    /// </summary>
    [JsonPropertyName("startMills")]
    public long StartMills => StartTime.ToMills();

    /// <summary>
    /// When the session ended in Unix milliseconds (computed for v1/v3 compatibility).
    /// </summary>
    [JsonPropertyName("endMills")]
    public long EndMills => EndTime.ToMills();

    /// <summary>
    /// IANA timezone where the sleep was recorded.
    /// </summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("type")]
    public SleepSessionType Type { get; set; }

    /// <summary>
    /// How the session boundaries were detected.
    /// </summary>
    [JsonPropertyName("detectionMethod")]
    public SleepDetectionMethod DetectionMethod { get; set; }

    /// <summary>
    /// Whether this is the user's primary overnight sleep.
    /// </summary>
    [JsonPropertyName("isMainSleep")]
    public bool? IsMainSleep { get; set; }

    /// <summary>
    /// Total duration of the session in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// Total time spent asleep in milliseconds.
    /// </summary>
    [JsonPropertyName("totalSleepMs")]
    public long TotalSleepMs { get; set; }

    /// <summary>
    /// Total time spent awake during the session in milliseconds.
    /// </summary>
    [JsonPropertyName("totalAwakeMs")]
    public long? TotalAwakeMs { get; set; }

    /// <summary>
    /// Total time in deep sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("deepSleepMs")]
    public long? DeepSleepMs { get; set; }

    /// <summary>
    /// Total time in light sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("lightSleepMs")]
    public long? LightSleepMs { get; set; }

    /// <summary>
    /// Total time in REM sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("remSleepMs")]
    public long? RemSleepMs { get; set; }

    /// <summary>
    /// Time from lights-out to sleep onset in milliseconds.
    /// </summary>
    [JsonPropertyName("sleepLatencyMs")]
    public long? SleepLatencyMs { get; set; }

    /// <summary>
    /// Sleep efficiency percentage (0-100).
    /// </summary>
    [JsonPropertyName("efficiency")]
    public float? Efficiency { get; set; }

    [JsonPropertyName("restlessPeriods")]
    public int? RestlessPeriods { get; set; }

    /// <summary>
    /// Device-computed sleep quality score.
    /// </summary>
    [JsonPropertyName("sleepScore")]
    public short? SleepScore { get; set; }

    /// <summary>
    /// Average heart rate during sleep in BPM.
    /// </summary>
    [JsonPropertyName("avgHeartRate")]
    public float? AvgHeartRate { get; set; }

    /// <summary>
    /// Minimum heart rate during sleep in BPM.
    /// </summary>
    [JsonPropertyName("minHeartRate")]
    public float? MinHeartRate { get; set; }

    /// <summary>
    /// Average heart rate variability during sleep in milliseconds.
    /// </summary>
    [JsonPropertyName("avgHrv")]
    public float? AvgHrv { get; set; }

    /// <summary>
    /// Average breathing rate during sleep in breaths per minute.
    /// </summary>
    [JsonPropertyName("avgBreathRate")]
    public float? AvgBreathRate { get; set; }

    /// <summary>
    /// Average blood oxygen saturation during sleep.
    /// </summary>
    [JsonPropertyName("avgSpo2")]
    public float? AvgSpo2 { get; set; }

    /// <summary>
    /// Origin platform that recorded the sleep data.
    /// </summary>
    [JsonPropertyName("source")]
    public SleepSource Source { get; set; }

    [JsonPropertyName("sourceDevice")]
    public string? SourceDevice { get; set; }

    /// <summary>
    /// Name of the application that submitted the data.
    /// </summary>
    [JsonPropertyName("sourceApp")]
    public string? SourceApp { get; set; }

    /// <summary>
    /// Original ID from the source system for deduplication.
    /// </summary>
    [JsonPropertyName("originalId")]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Source-specific metadata (stored as JSON).
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Sleep stage intervals within this session.
    /// </summary>
    [JsonPropertyName("stages")]
    public List<SleepStageInterval>? Stages { get; set; }

    /// <summary>
    /// Biometric samples collected during this session.
    /// </summary>
    [JsonPropertyName("biometricSamples")]
    public List<SleepBiometricSample>? BiometricSamples { get; set; }
}
