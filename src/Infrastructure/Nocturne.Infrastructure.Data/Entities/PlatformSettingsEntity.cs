using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Stores bot platform credentials (Discord, Slack, Telegram, WhatsApp) at the instance level.
/// Instance-wide — no tenant scoping. Credentials are stored encrypted; configured_fields
/// tracks which fields have values so the UI can show configuration status without exposing secrets.
/// </summary>
[Table("platform_settings")]
public class PlatformSettingsEntity
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Platform category identifier (e.g. "discord", "slack", "telegram").</summary>
    [Required]
    [MaxLength(100)]
    [Column("category")]
    public string Category { get; set; } = string.Empty;

    /// <summary>Whether this platform integration is currently active.</summary>
    [Column("enabled")]
    public bool Enabled { get; set; }

    /// <summary>All platform credentials stored as an encrypted JSON blob.</summary>
    [Column("encrypted_json", TypeName = "jsonb")]
    public string EncryptedJson { get; set; } = "{}";

    /// <summary>Names of credential fields that have been configured, for UI display without exposing values.</summary>
    [Column("configured_fields", TypeName = "jsonb")]
    public List<string> ConfiguredFields { get; set; } = [];

    /// <summary>UTC timestamp when this record was created.</summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    /// <summary>UTC timestamp when this record was last updated.</summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }
}
