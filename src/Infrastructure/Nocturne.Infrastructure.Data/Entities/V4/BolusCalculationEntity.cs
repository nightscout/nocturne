using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for bolus calculator/wizard records
/// Maps to Nocturne.Core.Models.V4.BolusCalculation
/// </summary>
[Table("bolus_calculations")]
public class BolusCalculationEntity : ITenantScoped, IAuditable, ISoftDeletable, IV4TimeSeriesEntity, ISystemTimestamped
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
    /// Device identifier that performed this calculation
    /// </summary>
    [Column("device")]
    [MaxLength(256)]
    public string? Device { get; set; }

    /// <summary>
    /// Application that uploaded this calculation
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
    [MaxLength(255)]
    public string? LegacyId { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [AuditIgnored]
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [AuditIgnored]
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Blood glucose input value used for the calculation
    /// </summary>
    [Column("blood_glucose_input")]
    public double? BloodGlucoseInput { get; set; }

    /// <summary>
    /// Source of blood glucose input (varies by APS system)
    /// </summary>
    [Column("blood_glucose_input_source")]
    [MaxLength(256)]
    public string? BloodGlucoseInputSource { get; set; }

    /// <summary>
    /// Carbohydrate input value in grams
    /// </summary>
    [Column("carb_input")]
    public double? CarbInput { get; set; }

    /// <summary>
    /// Insulin on board at the time of calculation
    /// </summary>
    [Column("insulin_on_board")]
    public double? InsulinOnBoard { get; set; }

    /// <summary>
    /// Recommended insulin dose from the calculator
    /// </summary>
    [Column("insulin_recommendation")]
    public double? InsulinRecommendation { get; set; }

    /// <summary>
    /// Carb-to-insulin ratio used in the calculation
    /// </summary>
    [Column("carb_ratio")]
    public double? CarbRatio { get; set; }

    /// <summary>
    /// How this calculation was determined (enum stored as string: Suggested, Manual, Automatic)
    /// </summary>
    [Column("calculation_type")]
    [MaxLength(32)]
    public string? CalculationType { get; set; }

    /// <summary>
    /// Recommended amount of insulin for carbohydrates.
    /// </summary>
    [Column("insulin_recommendation_for_carbs")]
    public double? InsulinRecommendationForCarbs { get; set; }

    /// <summary>
    /// Total amount of insulin programmed for delivery.
    /// </summary>
    [Column("insulin_programmed")]
    public double? InsulinProgrammed { get; set; }

    /// <summary>
    /// The amount of insulin entered by the user.
    /// </summary>
    [Column("entered_insulin")]
    public double? EnteredInsulin { get; set; }

    /// <summary>
    /// Portion of dual/square bolus to be delivered immediately.
    /// </summary>
    [Column("split_now")]
    public double? SplitNow { get; set; }

    /// <summary>
    /// Extended portion of dual/square bolus.
    /// </summary>
    [Column("split_ext")]
    public double? SplitExt { get; set; }

    /// <summary>
    /// Amount of insulin delivered as a pre-bolus.
    /// </summary>
    [Column("pre_bolus")]
    public double? PreBolus { get; set; }

    /// <summary>
    /// Catch-all JSONB column for fields not mapped to dedicated columns
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is treated as deleted
    /// by the global query filter and is invisible above the repository layer.
    /// </summary>
    [AuditIgnored]
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
