using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for a sleep session recorded by a first-party source.
/// Maps to Nocturne.Core.Models.SleepSession.
/// </summary>
[Table("sleep_sessions")]
public class SleepSessionEntity : ITenantScoped, IAuditable
{
    /// <summary>
    /// The unique identifier of the tenant this record belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification.
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// When the sleep session started (UTC).
    /// </summary>
    [Column("start_time")]
    public DateTime StartTime { get; set; }

    /// <summary>
    /// When the sleep session ended (UTC).
    /// </summary>
    [Column("end_time")]
    public DateTime EndTime { get; set; }

    /// <summary>
    /// IANA timezone of the sleeper at the time of the session.
    /// </summary>
    [Column("timezone")]
    [MaxLength(100)]
    public string? Timezone { get; set; }

    /// <summary>
    /// Sleep type (e.g. "Overnight", "Nap").
    /// </summary>
    [Column("type")]
    [MaxLength(20)]
    [Required]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// How the sleep session was detected (e.g. "Automatic", "Manual").
    /// </summary>
    [Column("detection_method")]
    [MaxLength(50)]
    [Required]
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is the primary overnight sleep session for the day.
    /// </summary>
    [Column("is_main_sleep")]
    public bool? IsMainSleep { get; set; }

    /// <summary>
    /// Total duration of the session in milliseconds (end - start).
    /// </summary>
    [Column("duration_ms")]
    public long DurationMs { get; set; }

    /// <summary>
    /// Total time asleep in milliseconds.
    /// </summary>
    [Column("total_sleep_ms")]
    public long TotalSleepMs { get; set; }

    /// <summary>
    /// Total time awake during the session in milliseconds.
    /// </summary>
    [Column("total_awake_ms")]
    public long? TotalAwakeMs { get; set; }

    /// <summary>
    /// Time in deep sleep in milliseconds.
    /// </summary>
    [Column("deep_sleep_ms")]
    public long? DeepSleepMs { get; set; }

    /// <summary>
    /// Time in light sleep in milliseconds.
    /// </summary>
    [Column("light_sleep_ms")]
    public long? LightSleepMs { get; set; }

    /// <summary>
    /// Time in REM sleep in milliseconds.
    /// </summary>
    [Column("rem_sleep_ms")]
    public long? RemSleepMs { get; set; }

    /// <summary>
    /// Time to fall asleep in milliseconds.
    /// </summary>
    [Column("sleep_latency_ms")]
    public long? SleepLatencyMs { get; set; }

    /// <summary>
    /// Sleep efficiency as a percentage (0-100).
    /// </summary>
    [Column("efficiency")]
    public float? Efficiency { get; set; }

    /// <summary>
    /// Number of restless or awakening periods during the session.
    /// </summary>
    [Column("restless_periods")]
    public int? RestlessPeriods { get; set; }

    /// <summary>
    /// Composite sleep quality score from the source.
    /// </summary>
    [Column("sleep_score")]
    public short? SleepScore { get; set; }

    /// <summary>
    /// Average heart rate during the session (bpm).
    /// </summary>
    [Column("avg_heart_rate")]
    public float? AvgHeartRate { get; set; }

    /// <summary>
    /// Minimum heart rate during the session (bpm).
    /// </summary>
    [Column("min_heart_rate")]
    public float? MinHeartRate { get; set; }

    /// <summary>
    /// Average heart rate variability during the session (ms).
    /// </summary>
    [Column("avg_hrv")]
    public float? AvgHrv { get; set; }

    /// <summary>
    /// Average breathing rate during the session (breaths/min).
    /// </summary>
    [Column("avg_breath_rate")]
    public float? AvgBreathRate { get; set; }

    /// <summary>
    /// Average blood oxygen saturation during the session (%).
    /// </summary>
    [Column("avg_spo2")]
    public float? AvgSpo2 { get; set; }

    /// <summary>
    /// Data source identifier (e.g. "fitbit", "apple_health").
    /// </summary>
    [Column("source")]
    [MaxLength(50)]
    [Required]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Device that recorded the session (e.g. "Fitbit Sense 2").
    /// </summary>
    [Column("source_device")]
    [MaxLength(200)]
    public string? SourceDevice { get; set; }

    /// <summary>
    /// Application that provided the data (e.g. "Fitbit iOS 4.12").
    /// </summary>
    [Column("source_app")]
    [MaxLength(200)]
    public string? SourceApp { get; set; }

    /// <summary>
    /// Original ID from the source system for deduplication.
    /// </summary>
    [Column("original_id")]
    [MaxLength(255)]
    public string? OriginalId { get; set; }

    /// <summary>
    /// Source-specific metadata stored as JSON.
    /// </summary>
    [Column("metadata", TypeName = "jsonb")]
    public string? MetadataJson { get; set; }

    /// <summary>
    /// System tracking: when record was created.
    /// </summary>
    [AuditIgnored]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated.
    /// </summary>
    [AuditIgnored]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Sleep stages within this session.
    /// </summary>
    public ICollection<SleepStageEntity> Stages { get; set; } = [];

    /// <summary>
    /// Biometric samples recorded during this session.
    /// </summary>
    public ICollection<SleepBiometricSampleEntity> BiometricSamples { get; set; } = [];
}
