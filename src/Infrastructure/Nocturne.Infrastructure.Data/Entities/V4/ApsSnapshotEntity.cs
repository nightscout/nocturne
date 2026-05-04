using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for APS algorithm snapshot records
/// Maps to Nocturne.Core.Models.V4.ApsSnapshot
/// </summary>
[Table("aps_snapshots")]
public class ApsSnapshotEntity : ITenantScoped
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
    /// Device identifier that produced this snapshot
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Links records that were decomposed from the same legacy DeviceStatus
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
    /// Which AID algorithm produced this snapshot (enum stored as string: Loop, Trio, ControlIQ, etc.)
    /// </summary>
    [Column("aps_system")]
    [MaxLength(32)]
    public string AidAlgorithm { get; set; } = null!;

    /// <summary>
    /// Total insulin on board
    /// </summary>
    [Column("iob")]
    public double? Iob { get; set; }

    /// <summary>
    /// Basal component of IOB
    /// </summary>
    [Column("basal_iob")]
    public double? BasalIob { get; set; }

    /// <summary>
    /// Bolus component of IOB
    /// </summary>
    [Column("bolus_iob")]
    public double? BolusIob { get; set; }

    /// <summary>
    /// Carbs on board
    /// </summary>
    [Column("cob")]
    public double? Cob { get; set; }

    /// <summary>
    /// Current blood glucose as seen by the algorithm
    /// </summary>
    [Column("current_bg")]
    public double? CurrentBg { get; set; }

    /// <summary>
    /// Predicted eventual BG if no further action
    /// </summary>
    [Column("eventual_bg")]
    public double? EventualBg { get; set; }

    /// <summary>
    /// Algorithm target BG
    /// </summary>
    [Column("target_bg")]
    public double? TargetBg { get; set; }

    /// <summary>
    /// Recommended bolus (insulinReq for OpenAPS, recommendedBolus for Loop)
    /// </summary>
    [Column("recommended_bolus")]
    public double? RecommendedBolus { get; set; }

    /// <summary>
    /// Autosens/dynamic ISF sensitivity ratio
    /// </summary>
    [Column("sensitivity_ratio")]
    public double? SensitivityRatio { get; set; }

    /// <summary>
    /// Whether the algorithm's suggestion was enacted (confirmed by pump)
    /// </summary>
    [Column("enacted")]
    public bool Enacted { get; set; }

    /// <summary>
    /// Enacted temp basal rate in U/hr
    /// </summary>
    [Column("enacted_rate")]
    public double? EnactedRate { get; set; }

    /// <summary>
    /// Enacted temp basal duration in minutes
    /// </summary>
    [Column("enacted_duration")]
    public int? EnactedDuration { get; set; }

    /// <summary>
    /// Enacted auto-bolus volume (SMB for OpenAPS, bolusVolume for Loop)
    /// </summary>
    [Column("enacted_bolus_volume")]
    public double? EnactedBolusVolume { get; set; }

    /// <summary>
    /// Full suggested/recommended JSON blob from the APS system (jsonb)
    /// </summary>
    [Column("suggested_json", TypeName = "jsonb")]
    public string? SuggestedJson { get; set; }

    /// <summary>
    /// Full enacted JSON blob from the APS system (jsonb)
    /// </summary>
    [Column("enacted_json", TypeName = "jsonb")]
    public string? EnactedJson { get; set; }

    /// <summary>
    /// Default prediction curve as JSON array (jsonb)
    /// </summary>
    [Column("predicted_default_json", TypeName = "jsonb")]
    public string? PredictedDefaultJson { get; set; }

    /// <summary>
    /// IOB-only prediction curve as JSON array (jsonb)
    /// </summary>
    [Column("predicted_iob_json", TypeName = "jsonb")]
    public string? PredictedIobJson { get; set; }

    /// <summary>
    /// Zero-temp prediction curve as JSON array (jsonb)
    /// </summary>
    [Column("predicted_zt_json", TypeName = "jsonb")]
    public string? PredictedZtJson { get; set; }

    /// <summary>
    /// COB prediction curve as JSON array (jsonb)
    /// </summary>
    [Column("predicted_cob_json", TypeName = "jsonb")]
    public string? PredictedCobJson { get; set; }

    /// <summary>
    /// UAM prediction curve as JSON array (jsonb)
    /// </summary>
    [Column("predicted_uam_json", TypeName = "jsonb")]
    public string? PredictedUamJson { get; set; }

    /// <summary>
    /// Timestamp of prediction start as UTC DateTime (timestamptz)
    /// </summary>
    [Column("predicted_start_timestamp")]
    public DateTime? PredictedStartTimestamp { get; set; }

    /// <summary>
    /// Full serialized Loop status object for round-trip fidelity
    /// </summary>
    [Column("loop_json", TypeName = "jsonb")]
    public string? LoopJson { get; set; }

    /// <summary>
    /// Algorithm version string (e.g. Trio app version)
    /// </summary>
    [Column("aid_version")]
    [MaxLength(64)]
    public string? AidVersion { get; set; }

    /// <summary>
    /// Foreign key to the Device table.
    /// </summary>
    [Column("device_id")]
    public Guid? DeviceId { get; set; }

    /// <summary>
    /// Foreign key to the PatientDevice table.
    /// </summary>
    [Column("patient_device_id")]
    public Guid? PatientDeviceId { get; set; }

    /// <summary>
    /// Catch-all JSONB column for fields not mapped to dedicated columns
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }
}
