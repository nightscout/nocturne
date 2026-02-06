using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for connector-imported food entries.
/// </summary>
[Table("connector_food_entries")]
public class ConnectorFoodEntryEntity : IHasSysCreatedAt, IHasSysUpdatedAt
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Source connector identifier (e.g., "myfitnesspal-connector")
    /// </summary>
    [Column("connector_source")]
    [MaxLength(50)]
    public string ConnectorSource { get; set; } = string.Empty;

    /// <summary>
    /// Entry ID from the connector source
    /// </summary>
    [Column("external_entry_id")]
    [MaxLength(255)]
    public string ExternalEntryId { get; set; } = string.Empty;

    /// <summary>
    /// Food ID from the connector source
    /// </summary>
    [Column("external_food_id")]
    [MaxLength(255)]
    public string ExternalFoodId { get; set; } = string.Empty;

    /// <summary>
    /// Linked food record in Nocturne
    /// </summary>
    [Column("food_id")]
    public Guid? FoodId { get; set; }

    /// <summary>
    /// When the food was consumed
    /// </summary>
    [Column("consumed_at")]
    public DateTimeOffset ConsumedAt { get; set; }

    /// <summary>
    /// When the food was logged in the connector source
    /// </summary>
    [Column("logged_at")]
    public DateTimeOffset? LoggedAt { get; set; }

    /// <summary>
    /// Meal name (Breakfast/Lunch/Dinner/Snack)
    /// </summary>
    [Column("meal_name")]
    [MaxLength(50)]
    public string MealName { get; set; } = string.Empty;

    /// <summary>
    /// Total carbs for the entry
    /// </summary>
    [Column("carbs")]
    public decimal Carbs { get; set; }

    /// <summary>
    /// Total protein for the entry
    /// </summary>
    [Column("protein")]
    public decimal Protein { get; set; }

    /// <summary>
    /// Total fat for the entry
    /// </summary>
    [Column("fat")]
    public decimal Fat { get; set; }

    /// <summary>
    /// Total energy (calories) for the entry
    /// </summary>
    [Column("energy")]
    public decimal Energy { get; set; }

    /// <summary>
    /// Number of servings for the entry
    /// </summary>
    [Column("servings")]
    public decimal Servings { get; set; }

    /// <summary>
    /// Serving description provided by the connector
    /// </summary>
    [Column("serving_description")]
    [MaxLength(100)]
    public string? ServingDescription { get; set; }

    /// <summary>
    /// Entry status (pending/matched/standalone/deleted)
    /// </summary>
    [Column("status")]
    [MaxLength(20)]
    public ConnectorFoodEntryStatus Status { get; set; } = ConnectorFoodEntryStatus.Pending;

    /// <summary>
    /// Treatment matched to this entry (when resolved)
    /// </summary>
    [Column("matched_treatment_id")]
    public Guid? MatchedTreatmentId { get; set; }

    /// <summary>
    /// When the entry status was resolved
    /// </summary>
    [Column("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; set; }

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
    /// Navigation property to food
    /// </summary>
    public FoodEntity? Food { get; set; }

    /// <summary>
    /// Navigation property to matched treatment
    /// </summary>
    public TreatmentEntity? MatchedTreatment { get; set; }
}
