using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for therapy settings (decomposed profile configuration)
/// Maps to Nocturne.Core.Models.V4.TherapySettings
/// </summary>
[Table("therapy_settings")]
public class TherapySettingsEntity : V4TimeSeriesEntityBase
{
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
}
