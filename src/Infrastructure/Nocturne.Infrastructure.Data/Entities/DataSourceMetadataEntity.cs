using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Entity for storing user preferences and metadata about data sources.
/// Data sources themselves are derived from entries/device status, but this
/// entity allows tracking user preferences like archive status.
/// </summary>
[Table("data_source_metadata")]
public class DataSourceMetadataEntity : ITenantScoped
{
    /// <summary>
    /// Identifier of the tenant this metadata belongs to
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Unique identifier for this metadata record
    /// </summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// The device identifier this metadata applies to.
    /// Matches the 'device' field from entries and device status records.
    /// </summary>
    [Required]
    [Column("device_id")]
    [MaxLength(255)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this data source has been archived by the user.
    /// Archived sources are hidden from the active data sources list.
    /// </summary>
    [Column("is_archived")]
    public bool IsArchived { get; set; }

    /// <summary>
    /// Timestamp when the data source was archived (null if not archived)
    /// </summary>
    [Column("archived_at")]
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>
    /// Optional user notes about this data source
    /// </summary>
    [Column("notes")]
    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Timestamp when this metadata record was created
    /// </summary>
    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp when this metadata record was last updated
    /// </summary>
    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
