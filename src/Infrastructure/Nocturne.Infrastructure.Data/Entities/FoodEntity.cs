using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Food entries
/// Maps to Nocturne.Core.Models.Food
/// </summary>
[Table("foods")]
public class FoodEntity : IHasSysCreatedAt, IHasSysUpdatedAt
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
    /// External source identifier for connector imports (e.g., "myfitnesspal")
    /// </summary>
    [Column("external_source")]
    [MaxLength(50)]
    public string? ExternalSource { get; set; }

    /// <summary>
    /// External food ID for connector imports
    /// </summary>
    [Column("external_id")]
    [MaxLength(255)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// Type of record ("food" or "quickpick")
    /// </summary>
    [Column("type")]
    [MaxLength(50)]
    public string Type { get; set; } = "food";

    /// <summary>
    /// Food category
    /// </summary>
    [Column("category")]
    [MaxLength(200)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Food subcategory
    /// </summary>
    [Column("subcategory")]
    [MaxLength(200)]
    public string Subcategory { get; set; } = string.Empty;

    /// <summary>
    /// Food name
    /// </summary>
    [Column("name")]
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Portion size
    /// </summary>
    [Column("portion")]
    public double Portion { get; set; }

    /// <summary>
    /// Carbohydrates in grams per portion
    /// </summary>
    [Column("carbs")]
    public double Carbs { get; set; }

    /// <summary>
    /// Fat content in grams per portion
    /// </summary>
    [Column("fat")]
    public double Fat { get; set; }

    /// <summary>
    /// Protein content in grams per portion
    /// </summary>
    [Column("protein")]
    public double Protein { get; set; }

    /// <summary>
    /// Energy content in kilojoules per portion
    /// </summary>
    [Column("energy")]
    public double Energy { get; set; }

    /// <summary>
    /// Glycemic index classification
    /// </summary>
    [Column("gi")]
    public GlycemicIndex Gi { get; set; } = GlycemicIndex.Medium;

    /// <summary>
    /// Unit of measurement (g, ml, pcs, oz, or serving description)
    /// </summary>
    [Column("unit")]
    [MaxLength(30)]
    public string Unit { get; set; } = "g";

    /// <summary>
    /// Foods included in a quickpick (JSON stored as string, only for type="quickpick")
    /// </summary>
    [Column("foods")]
    public string? Foods { get; set; }

    /// <summary>
    /// Whether to hide after use (only for type="quickpick")
    /// </summary>
    [Column("hide_after_use")]
    public bool HideAfterUse { get; set; }

    /// <summary>
    /// Whether the quickpick is hidden (only for type="quickpick")
    /// </summary>
    [Column("hidden")]
    public bool Hidden { get; set; }

    /// <summary>
    /// Display position for quickpicks (only for type="quickpick")
    /// </summary>
    [Column("position")]
    public int Position { get; set; } = 99999;

    /// <summary>
    /// System-generated creation timestamp for audit tracking
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    /// <summary>
    /// System-generated update timestamp for audit tracking
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }

    /// <summary>
    /// Additional properties from import (stored as JSON)
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }
}

/// <summary>
/// Glycemic index classification enumeration
/// </summary>
public enum GlycemicIndex
{
    /// <summary>
    /// Low glycemic index (1) - Slowly absorbed, minimal blood sugar impact
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium glycemic index (2) - Moderate absorption rate and blood sugar impact
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High glycemic index (3) - Rapidly absorbed, significant blood sugar impact
    /// </summary>
    High = 3,
}
