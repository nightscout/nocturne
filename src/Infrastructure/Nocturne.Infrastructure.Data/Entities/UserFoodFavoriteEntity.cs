using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for user food favorites.
/// </summary>
[Table("user_food_favorites")]
public class UserFoodFavoriteEntity : IHasSysCreatedAt
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this favorite belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to foods
    /// </summary>
    [Column("food_id")]
    public Guid FoodId { get; set; }

    /// <summary>
    /// System-generated creation timestamp
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    /// <summary>
    /// Navigation property to food
    /// </summary>
    public FoodEntity? Food { get; set; }
}
