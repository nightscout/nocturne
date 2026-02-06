using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for Settings entries
/// Maps to Nocturne.Core.Models.Settings
/// </summary>
[Table("settings")]
public class SettingsEntity : IHasSysCreatedAt, IHasSysUpdatedAt
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
    /// The setting key/name
    /// </summary>
    [Column("key")]
    [Required]
    [MaxLength(500)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The setting value as JSON string (can be any JSON value)
    /// </summary>
    [Column("value")]
    public string? Value { get; set; }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    [Column("created_at")]
    [MaxLength(50)]
    public string? CreatedAt { get; set; }

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch
    /// </summary>
    [Column("mills")]
    public long Mills { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [Column("utc_offset")]
    public int? UtcOffset { get; set; }

    /// <summary>
    /// Server timestamp when the setting was created (Unix timestamp in milliseconds)
    /// </summary>
    [Column("srv_created")]
    public DateTimeOffset? SrvCreated { get; set; }

    /// <summary>
    /// Server timestamp when the setting was last modified (Unix timestamp in milliseconds)
    /// </summary>
    [Column("srv_modified")]
    public DateTimeOffset? SrvModified { get; set; }

    /// <summary>
    /// Optional app field indicating which application created/modified this setting
    /// </summary>
    [Column("app")]
    [MaxLength(200)]
    public string? App { get; set; }

    /// <summary>
    /// Optional device field indicating which device created/modified this setting
    /// </summary>
    [Column("device")]
    [MaxLength(200)]
    public string? Device { get; set; }

    /// <summary>
    /// User that created or last modified this setting
    /// </summary>
    [Column("entered_by")]
    [MaxLength(200)]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Version or revision number for this setting
    /// </summary>
    [Column("version")]
    public int? Version { get; set; }

    /// <summary>
    /// Whether this setting is currently active/enabled
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional notes or description for this setting
    /// </summary>
    [Column("notes")]
    [MaxLength(1000)]
    public string? Notes { get; set; }

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
