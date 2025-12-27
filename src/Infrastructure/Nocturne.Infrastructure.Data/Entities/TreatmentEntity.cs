using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Treatment
/// Maps to Nocturne.Core.Models.Treatment
/// </summary>
[Table("treatments")]
public class TreatmentEntity
{
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
    /// Glucose value for the treatment
    /// </summary>
    [Column("glucose")]
    public double? Glucose { get; set; }

    /// <summary>
    /// Glucose type (e.g., "Finger", "Sensor")
    /// </summary>
    [Column("glucoseType")]
    [MaxLength(50)]
    public string? GlucoseType { get; set; }

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
    /// Protein content in grams
    /// </summary>
    [Column("protein")]
    public double? Protein { get; set; }

    /// <summary>
    /// Fat content in grams
    /// </summary>
    [Column("fat")]
    public double? Fat { get; set; }

    /// <summary>
    /// Food type
    /// </summary>
    [Column("foodType")]
    [MaxLength(255)]
    public string? FoodType { get; set; }

    /// <summary>
    /// Units (e.g., "mg/dl", "mmol")
    /// </summary>
    [Column("units")]
    [MaxLength(10)]
    public string? Units { get; set; }

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
    /// Treatment duration in minutes
    /// </summary>
    [Column("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Percent of temporary basal rate
    /// </summary>
    [Column("percent")]
    public double? Percent { get; set; }

    /// <summary>
    /// Absolute temporary basal rate
    /// </summary>
    [Column("absolute")]
    public double? Absolute { get; set; }

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
    /// Treatment profile
    /// </summary>
    [Column("profile")]
    [MaxLength(255)]
    public string? Profile { get; set; }

    /// <summary>
    /// Whether this entry was split from another
    /// </summary>
    [Column("split")]
    [MaxLength(255)]
    public string? Split { get; set; }

    /// <summary>
    /// When this treatment was created (timestamp)
    /// </summary>
    [Column("date")]
    public long? Date { get; set; }

    /// <summary>
    /// Carb time offset
    /// </summary>
    [Column("carbTime")]
    public int? CarbTime { get; set; }

    /// <summary>
    /// Bolus calculator values (stored as JSON)
    /// </summary>
    [Column("boluscalc", TypeName = "jsonb")]
    public string? BolusCalcJson { get; set; }

    /// <summary>
    /// UTC offset
    /// </summary>
    [Column("utcOffset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [Column("timestamp")]
    public long? Timestamp { get; set; }

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
    /// Event time as ISO string (used by Glooko connector)
    /// </summary>
    [Column("eventTime")]
    [MaxLength(50)]
    public string? EventTime { get; set; }

    /// <summary>
    /// Pre-bolus time in minutes
    /// </summary>
    [Column("preBolus")]
    public double? PreBolus { get; set; }

    /// <summary>
    /// Basal rate (used for temp basal treatments)
    /// </summary>
    [Column("rate")]
    public double? Rate { get; set; }

    /// <summary>
    /// Blood glucose value in mg/dL
    /// </summary>
    [Column("mgdl")]
    public double? Mgdl { get; set; }

    /// <summary>
    /// Blood glucose value in mmol/L
    /// </summary>
    [Column("mmol")]
    public double? Mmol { get; set; }

    /// <summary>
    /// End time in milliseconds for duration treatments
    /// </summary>
    [Column("endmills")]
    public long? EndMills { get; set; }

    /// <summary>
    /// Duration type (e.g., "indefinite")
    /// </summary>
    [Column("durationType")]
    [MaxLength(50)]
    public string? DurationType { get; set; }

    /// <summary>
    /// Whether this treatment is an announcement
    /// </summary>
    [Column("isAnnouncement")]
    public bool? IsAnnouncement { get; set; }

    /// <summary>
    /// JSON string of profile data for profile switches
    /// </summary>
    [Column("profileJson", TypeName = "jsonb")]
    public string? ProfileJson { get; set; }

    /// <summary>
    /// End profile name for profile switches
    /// </summary>
    [Column("endprofile")]
    [MaxLength(255)]
    public string? EndProfile { get; set; }

    /// <summary>
    /// Insulin scaling factor for adjustments
    /// </summary>
    [Column("insulinNeedsScaleFactor")]
    public double? InsulinNeedsScaleFactor { get; set; }

    /// <summary>
    /// Carb absorption time in minutes
    /// </summary>
    [Column("absorptionTime")]
    public int? AbsorptionTime { get; set; }

    /// <summary>
    /// Manually entered insulin amount
    /// </summary>
    [Column("enteredinsulin")]
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered immediately
    /// </summary>
    [Column("splitNow")]
    public double? SplitNow { get; set; }

    /// <summary>
    /// Percentage of combo bolus delivered extended
    /// </summary>
    [Column("splitExt")]
    public double? SplitExt { get; set; }

    /// <summary>
    /// Treatment status
    /// </summary>
    [Column("status")]
    [MaxLength(255)]
    public string? Status { get; set; }

    /// <summary>
    /// Relative basal rate change
    /// </summary>
    [Column("relative")]
    public double? Relative { get; set; }

    /// <summary>
    /// Carb ratio
    /// </summary>
    [Column("CR")]
    public double? CR { get; set; }

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
    /// Whether this is a CircadianPercentageProfile treatment
    /// </summary>
    [Column("CircadianPercentageProfile")]
    public bool? CircadianPercentageProfile { get; set; }

    /// <summary>
    /// Percentage for CircadianPercentageProfile
    /// </summary>
    [Column("percentage")]
    public double? Percentage { get; set; }

    /// <summary>
    /// Timeshift for CircadianPercentageProfile (in hours)
    /// </summary>
    [Column("timeshift")]
    public double? Timeshift { get; set; }

    /// <summary>
    /// Transmitter ID (used by CGM devices)
    /// </summary>
    [Column("transmitterId")]
    [MaxLength(255)]
    public string? TransmitterId { get; set; }

    /// <summary>
    /// Data source identifier indicating where this treatment originated from.
    /// Use constants from Nocturne.Core.Constants.DataSources for consistent values.
    /// Examples: "demo-service", "dexcom-connector", "manual", "mongodb-import"
    /// </summary>
    [Column("data_source")]
    [MaxLength(50)]
    public string? DataSource { get; set; }

    // === APS/Bolus Calculator Fields ===

    /// <summary>
    /// Insulin recommended by bolus calculator specifically for carbohydrate coverage
    /// </summary>
    [Column("insulin_recommendation_for_carbs")]
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Insulin recommended by bolus calculator for glucose correction
    /// </summary>
    [Column("insulin_recommendation_for_correction")]
    public double? InsulinRecommendationForCorrection { get; set; }

    /// <summary>
    /// Total insulin amount programmed for delivery
    /// </summary>
    [Column("insulin_programmed")]
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// Actual insulin amount delivered
    /// </summary>
    [Column("insulin_delivered")]
    public double? InsulinDelivered { get; set; }

    /// <summary>
    /// Insulin on board at the time of this treatment
    /// </summary>
    [Column("insulin_on_board")]
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Blood glucose input value used for bolus calculation
    /// </summary>
    [Column("blood_glucose_input")]
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of blood glucose input (e.g., "Finger", "Sensor", "CGM")
    /// </summary>
    [Column("blood_glucose_input_source")]
    [MaxLength(50)]
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// How this bolus was calculated/initiated (Suggested, Manual, Automatic)
    /// </summary>
    [Column("calculation_type")]
    [MaxLength(20)]
    public string? CalculationType { get; set; }

    /// <summary>
    /// Remote carb entry amount in grams (for Loop remote commands)
    /// </summary>
    [Column("remoteCarbs")]
    public double? RemoteCarbs { get; set; }

    /// <summary>
    /// Remote carb absorption time in hours (for Loop remote commands)
    /// </summary>
    [Column("remoteAbsorption")]
    public double? RemoteAbsorption { get; set; }

    /// <summary>
    /// Remote bolus amount in units (for Loop remote commands)
    /// </summary>
    [Column("remoteBolus")]
    public double? RemoteBolus { get; set; }

    /// <summary>
    /// Display name for override reason
    /// </summary>
    [Column("reasonDisplay")]
    [MaxLength(255)]
    public string? ReasonDisplay { get; set; }

    /// <summary>
    /// One-time password for secure remote operations
    /// </summary>
    [Column("otp")]
    [MaxLength(255)]
    public string? Otp { get; set; }

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
    /// Additional properties from import (stored as JSON)
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }
}
