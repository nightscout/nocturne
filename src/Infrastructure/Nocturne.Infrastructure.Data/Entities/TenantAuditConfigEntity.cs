using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Platform-level audit configuration for a tenant.
/// Controls whether read-access logging is enabled and retention periods.
/// Intentionally NOT ITenantScoped — this is platform config (like TenantEntity itself),
/// not tenant-scoped PHI. The retention service reads all tenants' configs at once.
/// No RLS policy on this table.
/// </summary>
[Table("tenant_audit_config")]
public class TenantAuditConfigEntity
{
    /// <summary>Primary key (UUID v7).</summary>
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    /// <summary>FK to the tenant this configuration applies to.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>Whether read-access audit logging is enabled for this tenant.</summary>
    [Column("read_audit_enabled")]
    public bool ReadAuditEnabled { get; set; }

    /// <summary>Number of days to retain read audit log entries before purging.</summary>
    [Column("read_audit_retention_days")]
    public int? ReadAuditRetentionDays { get; set; }

    /// <summary>Number of days to retain mutation audit log entries before purging.</summary>
    [Column("mutation_audit_retention_days")]
    public int? MutationAuditRetentionDays { get; set; }

    /// <summary>UTC timestamp when this record was created.</summary>
    [Column("sys_created_at")]
    public DateTime SysCreatedAt { get; set; }

    /// <summary>UTC timestamp when this record was last updated.</summary>
    [Column("sys_updated_at")]
    public DateTime SysUpdatedAt { get; set; }

    /// <summary>Navigation property to the owning tenant.</summary>
    [ForeignKey(nameof(TenantId))]
    public TenantEntity Tenant { get; set; } = null!;
}
