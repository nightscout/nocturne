using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for physical device records
/// Maps to Nocturne.Core.Models.V4.Device
/// </summary>
[Table("devices")]
public class DeviceEntity : ITenantScoped, ISoftDeletable
{
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
    /// Device category discriminator (stored as string)
    /// </summary>
    [Column("category")]
    [MaxLength(32)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Device type/model name
    /// </summary>
    [Column("type")]
    [MaxLength(128)]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Device serial number
    /// </summary>
    [Column("serial")]
    [MaxLength(128)]
    public string Serial { get; set; } = string.Empty;

    /// <summary>
    /// When this device was first seen as UTC DateTime (timestamptz)
    /// </summary>
    [Column("first_seen_timestamp")]
    public DateTime FirstSeenTimestamp { get; set; }

    /// <summary>
    /// When this device was last seen as UTC DateTime (timestamptz)
    /// </summary>
    [Column("last_seen_timestamp")]
    public DateTime LastSeenTimestamp { get; set; }

    /// <summary>
    /// Catch-all JSONB column for fields not mapped to dedicated columns
    /// </summary>
    [Column("additional_properties", TypeName = "jsonb")]
    public string? AdditionalPropertiesJson { get; set; }

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is treated as deleted
    /// by the global query filter and is invisible above the repository layer.
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
