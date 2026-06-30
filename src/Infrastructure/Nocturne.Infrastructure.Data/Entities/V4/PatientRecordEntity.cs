using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for patient record data (diabetes type, diagnosis, demographics)
/// Maps to Nocturne.Core.Models.V4.PatientRecord
/// </summary>
[Table("patient_records")]
public class PatientRecordEntity : ITenantScoped, ISoftDeletable, ISystemTimestamped
{
    /// <summary>
    /// The unique identifier of the tenant this record belongs to.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Type of diabetes stored as string (e.g. "Type1", "Type2", "LADA")
    /// </summary>
    [Column("diabetes_type")]
    [MaxLength(32)]
    public string? DiabetesType { get; set; }

    /// <summary>
    /// Free-text description when DiabetesType is "Other"
    /// </summary>
    [Column("diabetes_type_other")]
    [MaxLength(256)]
    public string? DiabetesTypeOther { get; set; }

    /// <summary>
    /// Date of diabetes diagnosis
    /// </summary>
    [Column("diagnosis_date")]
    public DateOnly? DiagnosisDate { get; set; }

    /// <summary>
    /// Patient date of birth
    /// </summary>
    [Column("date_of_birth")]
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// Patient preferred name
    /// </summary>
    [Column("preferred_name")]
    [MaxLength(256)]
    public string? PreferredName { get; set; }

    /// <summary>
    /// Patient pronouns
    /// </summary>
    [Column("pronouns")]
    [MaxLength(64)]
    public string? Pronouns { get; set; }

    /// <summary>
    /// URL to patient avatar image
    /// </summary>
    [Column("avatar_url")]
    [MaxLength(2048)]
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// IANA timezone id (e.g. "Australia/Sydney"). Canonical source of patient timezone,
    /// replacing the deprecated per-profile <c>therapy_settings.timezone</c>. Drives
    /// wall-clock interpretation in alerts (time-of-day windows, DND, glucose-bucket
    /// schedule lookup) and analytics. Null until the patient sets it; readers must
    /// fall back gracefully.
    /// </summary>
    [Column("timezone")]
    [MaxLength(64)]
    public string? Timezone { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is treated as deleted
    /// by the global query filter and is invisible above the repository layer.
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
