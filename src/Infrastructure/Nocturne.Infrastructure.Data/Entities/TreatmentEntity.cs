using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Infrastructure.Data.Entities.OwnedTypes;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Treatment
/// Maps to Nocturne.Core.Models.Treatment
/// </summary>
[Table("treatments")]
public class TreatmentEntity
{
    // === Identity ===

    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Original MongoDB ObjectId as string for reference/migration tracking
    /// </summary>
    [Column("original_id")]
    [MaxLength(24)]
    public string? OriginalId { get; set; }

    // === Core Treatment Fields ===

    /// <summary>
    /// Event type (e.g., "Meal Bolus", "Correction Bolus")
    /// </summary>
    [Column("eventType")]
    [MaxLength(255)]
    public string? EventType { get; set; }

    /// <summary>
    /// Treatment reason
    /// </summary>
    [Column("reason")]
    public string? Reason { get; set; }

    /// <summary>
    /// Carbohydrates in grams
    /// </summary>
    [Column("carbs")]
    public double? Carbs { get; set; }

    /// <summary>
    /// Insulin amount in units
    /// </summary>
    [Column("insulin")]
    public double? Insulin { get; set; }

    /// <summary>
    /// Treatment duration in minutes
    /// </summary>
    [Column("duration")]
    public double? Duration { get; set; }

    // === Timestamps ===

    /// <summary>
    /// Time in milliseconds since Unix epoch
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// Created at timestamp as ISO string
    /// </summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? Created_at { get; set; }

    /// <summary>
    /// When this treatment was created (timestamp)
    /// </summary>
    [Column("date")]
    public long? Date { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [Column("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>
    /// UTC offset
    /// </summary>
    [Column("utcOffset")]
    public int? UtcOffset { get; set; }

    // === Metadata ===

    /// <summary>
    /// Treatment notes
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Who entered the treatment
    /// </summary>
    [Column("enteredBy")]
    [MaxLength(255)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Treatment status
    /// </summary>
    [Column("status")]
    [MaxLength(255)]
    public string? Status { get; set; }

    /// <summary>
    /// Treatment target top
    /// </summary>
    [Column("targetTop")]
    public double? TargetTop { get; set; }

    /// <summary>
    /// Treatment target bottom
    /// </summary>
    [Column("targetBottom")]
    public double? TargetBottom { get; set; }

    /// <summary>
    /// Whether this entry was split from another
    /// </summary>
    [Column("split")]
    [MaxLength(255)]
    public string? Split { get; set; }

    /// <summary>
    /// Profile name that cut this treatment
    /// </summary>
    [Column("cuttedby")]
    [MaxLength(255)]
    public string? CuttedBy { get; set; }

    /// <summary>
    /// Profile name that this treatment cut
    /// </summary>
    [Column("cutting")]
    [MaxLength(255)]
    public string? Cutting { get; set; }

    /// <summary>
    /// Nightscout client identifier
    /// </summary>
    [Column("NSCLIENT_ID")]
    [MaxLength(255)]
    public string? NsClientId { get; set; }

    /// <summary>
    /// Whether this is the first treatment in a series
    /// </summary>
    [Column("first")]
    public bool? First { get; set; }

    /// <summary>
    /// Whether this is the end treatment in a series
    /// </summary>
    [Column("end")]
    public bool? End { get; set; }

    /// <summary>
    /// Transmitter ID (used by CGM devices)
    /// </summary>
    [Column("transmitterId")]
    [MaxLength(255)]
    public string? TransmitterId { get; set; }

    /// <summary>
    /// Event time as ISO string (used by Glooko connector)
    /// </summary>
    [Column("eventTime")]
    [MaxLength(50)]
    public string? EventTime { get; set; }

    /// <summary>
    /// Whether this treatment is an announcement
    /// </summary>
    [Column("isAnnouncement")]
    public bool? IsAnnouncement { get; set; }

    /// <summary>
    /// Data source identifier indicating where this treatment originated from.
    /// Use constants from Nocturne.Core.Constants.DataSources for consistent values.
    /// Examples: "demo-service", "dexcom-connector", "manual", "mongodb-import"
    /// </summary>
    [Column("data_source")]
    [MaxLength(50)]
    public string? DataSource { get; set; }

    /// <summary>
    /// Additional properties from import (stored as JSON)
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }

    // === System Tracking ===

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

    // === Owned Types ===
    // Column mappings configured via TreatmentEntityConfiguration.ConfigureOwnedTypes()

    /// <summary>
    /// Glucose reading data (glucose value, type, mg/dL, mmol/L, units)
    /// </summary>
    public TreatmentGlucoseData GlucoseData { get; set; } = new();

    /// <summary>
    /// Nutritional/food data (protein, fat, food type, carb time, absorption time)
    /// </summary>
    public TreatmentNutritionalData Nutritional { get; set; } = new();

    /// <summary>
    /// Basal delivery data (rate, percent, absolute, relative, duration type, end mills)
    /// </summary>
    public TreatmentBasalData Basal { get; set; } = new();

    /// <summary>
    /// Bolus calculator data (recommendations, programmed/delivered insulin, BG input, calc type)
    /// </summary>
    public TreatmentBolusCalcData BolusCalc { get; set; } = new();

    /// <summary>
    /// Profile switch data (profile name/JSON, circadian percentage, timeshift)
    /// </summary>
    public TreatmentProfileData ProfileData { get; set; } = new();

    /// <summary>
    /// AndroidAPS-specific data (pump info, validity flags, original values)
    /// </summary>
    public TreatmentAapsData Aaps { get; set; } = new();

    /// <summary>
    /// Loop remote command data (remote carbs/bolus, OTP, reason display)
    /// </summary>
    public TreatmentLoopData Loop { get; set; } = new();
}
