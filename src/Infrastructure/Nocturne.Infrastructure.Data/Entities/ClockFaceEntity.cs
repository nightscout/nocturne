using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for ClockFace (saved clock face configuration)
/// Stores user-created clock face layouts with drag-and-drop configured elements
/// </summary>
[Table("clock_faces")]
public class ClockFaceEntity
{
    /// <summary>
    /// Primary key - UUID v7 serves as unguessable public URL
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// User identifier this clock face belongs to
    /// </summary>
    [Column("user_id")]
    [MaxLength(255)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for the clock face
    /// </summary>
    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Clock face configuration as JSONB (rows, elements, settings)
    /// </summary>
    [Column("config", TypeName = "jsonb")]
    public string ConfigJson { get; set; } = "{}";

    /// <summary>
    /// When this clock face was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this clock face was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// System tracking - when the record was created in PostgreSQL
    /// </summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking - when the record was last updated in PostgreSQL
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;
}
