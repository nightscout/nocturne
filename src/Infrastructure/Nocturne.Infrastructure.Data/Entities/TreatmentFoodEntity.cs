using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for food attribution entries linked to carb intake records.
/// </summary>
[Table("treatment_foods")]
public class TreatmentFoodEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to carb_intakes
    /// </summary>
    [Column("carb_intake_id")]
    public Guid CarbIntakeId { get; set; }

    /// <summary>
    /// Foreign key to foods (nullable for "other" entries)
    /// </summary>
    [Column("food_id")]
    public Guid? FoodId { get; set; }

    /// <summary>
    /// Number of portions
    /// </summary>
    [Column("portions")]
    public decimal Portions { get; set; }

    /// <summary>
    /// Carbohydrates in grams
    /// </summary>
    [Column("carbs")]
    public decimal Carbs { get; set; }

    /// <summary>
    /// Offset from carb intake timestamp in minutes
    /// </summary>
    [Column("time_offset_minutes")]
    public int TimeOffsetMinutes { get; set; }

    /// <summary>
    /// Optional note (especially for "other" entries)
    /// </summary>
    [Column("note")]
    [MaxLength(1000)]
    public string? Note { get; set; }

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
    /// Navigation property to carb intake
    /// </summary>
    public CarbIntakeEntity? CarbIntake { get; set; }

    /// <summary>
    /// Navigation property to food
    /// </summary>
    public FoodEntity? Food { get; set; }
}
