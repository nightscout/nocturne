using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for storing connector configuration.
/// Runtime-configurable properties are stored as JSON in ConfigurationJson.
/// Secret properties (passwords, API keys) are stored encrypted in SecretsJson.
/// </summary>
[Table("connector_configurations")]
public class ConnectorConfigurationEntity : ITenantScoped, ISystemTimestamped
{
    /// <summary>
    /// Identifier of the tenant this connector configuration belongs to
    /// </summary>
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
    /// The connector name (e.g., "Dexcom", "Glooko", "LibreLinkUp")
    /// </summary>
    [Column("connector_name")]
    [Required]
    [MaxLength(100)]
    public string ConnectorName { get; set; } = string.Empty;

    /// <summary>
    /// Runtime configuration as JSON (non-secret properties marked with [RuntimeConfigurable])
    /// </summary>
    [Column("configuration", TypeName = "jsonb")]
    public string ConfigurationJson { get; set; } = "{}";

    /// <summary>
    /// Encrypted secrets as JSON (properties marked with [Secret], encrypted with AES-256-GCM)
    /// Each secret value is stored as: base64(nonce || ciphertext || tag)
    /// </summary>
    [Column("secrets", TypeName = "jsonb")]
    public string SecretsJson { get; set; } = "{}";

    /// <summary>
    /// Schema version for migration support when configuration structure changes
    /// </summary>
    [Column("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// When the configuration was last modified
    /// </summary>
    [Column("last_modified")]
    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// Who last modified the configuration (user email, "system", etc.)
    /// </summary>
    [Column("modified_by")]
    [MaxLength(200)]
    public string? ModifiedBy { get; set; }

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
    /// When the connector last attempted to sync
    /// </summary>
    [Column("last_sync_attempt")]
    public DateTime? LastSyncAttempt { get; set; }

    /// <summary>
    /// When the connector last successfully completed a sync
    /// </summary>
    [Column("last_successful_sync")]
    public DateTime? LastSuccessfulSync { get; set; }

    /// <summary>
    /// The error message from the most recent failure
    /// </summary>
    [Column("last_error_message")]
    [MaxLength(1000)]
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// When the error occurred
    /// </summary>
    [Column("last_error_at")]
    public DateTime? LastErrorAt { get; set; }

    /// <summary>
    /// Current health status
    /// </summary>
    [Column("is_healthy")]
    public bool IsHealthy { get; set; } = true;
}
