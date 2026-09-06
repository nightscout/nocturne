using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for a biometric sample recorded during a sleep session.
/// Maps to Nocturne.Core.Models.SleepBiometricSample.
/// </summary>
[Table("sleep_biometric_samples")]
public class SleepBiometricSampleEntity : ITenantScoped
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
    /// Foreign key to the parent sleep session.
    /// </summary>
    [Column("sleep_session_id")]
    public Guid SleepSessionId { get; set; }

    /// <summary>
    /// When this sample was recorded (UTC).
    /// </summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Heart rate at this sample point (bpm).
    /// </summary>
    [Column("heart_rate")]
    public float? HeartRate { get; set; }

    /// <summary>
    /// Heart rate variability at this sample point (ms).
    /// </summary>
    [Column("hrv")]
    public float? Hrv { get; set; }

    /// <summary>
    /// Blood oxygen saturation at this sample point (%).
    /// </summary>
    [Column("spo2")]
    public float? Spo2 { get; set; }

    /// <summary>
    /// Respiration rate at this sample point (breaths/min).
    /// </summary>
    [Column("respiration_rate")]
    public float? RespirationRate { get; set; }

    /// <summary>
    /// Movement intensity at this sample point.
    /// </summary>
    [Column("movement")]
    public float? Movement { get; set; }

    /// <summary>
    /// Navigation to the parent sleep session.
    /// </summary>
    [ForeignKey(nameof(SleepSessionId))]
    public SleepSessionEntity SleepSession { get; set; } = null!;
}
