using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Entities.V4;

/// <summary>
/// PostgreSQL entity for uncaptured devicestatus sub-objects.
/// Maps to Nocturne.Core.Models.V4.DeviceStatusExtras
/// </summary>
[Table("device_status_extras")]
public class DeviceStatusExtrasEntity : ITenantScoped, IAuditable
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
    /// Links back to the originating DeviceStatus decomposition batch
    /// </summary>
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
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// System tracking: when record was last updated
    /// </summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; } = DateTime.UtcNow;
}
