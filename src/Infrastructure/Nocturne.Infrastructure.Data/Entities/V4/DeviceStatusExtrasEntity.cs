using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for uncaptured devicestatus sub-objects.
/// Maps to Nocturne.Core.Models.V4.DeviceStatusExtras
/// </summary>
[Table("device_status_extras")]
public class DeviceStatusExtrasEntity : ITenantScoped, IAuditable, ISoftDeletable, ISystemTimestamped
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
    /// Links back to the originating DeviceStatus decomposition batch. Unlike
    /// <see cref="IV4Entity.CorrelationId"/> this is non-nullable and is the row's only identity
    /// besides <see cref="Id"/>: an extras row is pure decomposition leftover with no legacy id,
    /// and the decomposer skips it when the batch has no correlation, so every row has one. Reads,
    /// deletes and both dedup passes key on it, which the nullable interface member cannot express
    /// — hence this entity stays outside <see cref="IV4Entity"/>.
    /// </summary>
    [AuditIgnored]
    [Column("correlation_id")]
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Canonical timestamp as UTC DateTime (timestamptz)
    /// </summary>
    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Catch-all JSONB column for uncaptured sub-objects
    /// </summary>
    [Column("extras", TypeName = "jsonb")]
    public string? ExtrasJson { get; set; }

    /// <summary>
    /// System tracking: when record was inserted
    /// </summary>
    [AuditIgnored]
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [AuditIgnored]
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Soft-delete timestamp. When non-null the record is treated as deleted
    /// by the global query filter and is invisible above the repository layer.
    /// </summary>
    [AuditIgnored]
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }
}
