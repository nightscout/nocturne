using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for therapy settings (decomposed profile configuration)
/// Maps to Nocturne.Core.Models.V4.TherapySettings
/// </summary>
[Table("therapy_settings")]
public class TherapySettingsEntity : ITenantScoped, IAuditable, ISoftDeletable, IV4TimeSeriesEntity, ISystemTimestamped
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
    /// Canonical timestamp as UTC DateTime (timestamptz)
    /// </summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Device identifier that created this settings record
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this settings record
    /// </summary>
    [Column("app")]
    [MaxLength(256)]
    public string? App { get; set; }

    /// <summary>
    /// Origin data source identifier
    /// </summary>
    [Column("data_source")]
    [MaxLength(256)]
    public string? DataSource { get; set; }

    /// <summary>
    /// Links records that were split from the same legacy Treatment
    /// </summary>
    [Column("correlation_id")]
    public Guid? CorrelationId { get; set; }

    /// <summary>
    /// Original v1/v3 record ID for migration traceability
    /// </summary>
    [Column("legacy_id")]
    [MaxLength(64)]
    public string? LegacyId { get; set; }

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
    /// Profile name this therapy settings record belongs to
    /// </summary>
    [Column("profile_name")]
    [MaxLength(100)]
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// IANA timezone identifier (e.g. "America/New_York")
    /// </summary>
    [Column("timezone")]
    [MaxLength(64)]
    public string? Timezone { get; set; }

    /// <summary>
    /// Glucose display units (e.g. "mg/dl", "mmol/L")
    /// </summary>
    [Column("units")]
    [MaxLength(10)]
    public string? Units { get; set; }

    /// <summary>
    /// Duration of insulin action in hours
    /// </summary>
    [Column("dia")]
    public double Dia { get; set; }

    /// <summary>
    /// Carb absorption rate in grams per hour
    /// </summary>
    [Column("carbs_hr")]
    public int CarbsHr { get; set; }

    /// <summary>
    /// Delay in minutes before carb absorption starts
    /// </summary>
    [Column("delay")]
    public int Delay { get; set; }

    /// <summary>
    /// Whether per-GI absorption values are used
    /// </summary>
    [Column("per_gi_values")]
    public bool? PerGiValues { get; set; }

    /// <summary>
    /// Carb absorption rate for high-GI foods (grams per hour)
    /// </summary>
    [Column("carbs_hr_high")]
    public int? CarbsHrHigh { get; set; }

    /// <summary>
    /// Carb absorption rate for medium-GI foods (grams per hour)
    /// </summary>
    [Column("carbs_hr_medium")]
    public int? CarbsHrMedium { get; set; }

    /// <summary>
    /// Carb absorption rate for low-GI foods (grams per hour)
    /// </summary>
    [Column("carbs_hr_low")]
    public int? CarbsHrLow { get; set; }

    /// <summary>
    /// Absorption delay for high-GI foods (minutes)
    /// </summary>
    [Column("delay_high")]
    public int? DelayHigh { get; set; }

    /// <summary>
    /// Absorption delay for medium-GI foods (minutes)
    /// </summary>
    [Column("delay_medium")]
    public int? DelayMedium { get; set; }

    /// <summary>
    /// Absorption delay for low-GI foods (minutes)
    /// </summary>
    [Column("delay_low")]
    public int? DelayLow { get; set; }

    /// <summary>
    /// Loop/APS system settings stored as JSONB
    /// </summary>
    [Column("loop_settings_json", TypeName = "jsonb")]
    public string? LoopSettingsJson { get; set; }

    /// <summary>
    /// Whether this is the default/active profile
    /// </summary>
    [Column("is_default")]
    public bool IsDefault { get; set; }

    /// <summary>
    /// User or system that created/modified this settings record
    /// </summary>
    [Column("entered_by")]
    [MaxLength(100)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Whether this profile is managed by an external system (e.g. pump)
    /// </summary>
    [Column("is_externally_managed")]
    public bool IsExternallyManaged { get; set; }

    /// <summary>
    /// ISO date string indicating when the profile became active
    /// </summary>
    [Column("start_date")]
    [MaxLength(50)]
    public string? StartDate { get; set; }

    /// <summary>
    /// Catch-all JSONB column for fields not mapped to dedicated columns
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is treated as deleted
    /// by the global query filter and is invisible above the repository layer.
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
