using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// Per-tenant configuration for soft-delete data retention.
/// </summary>
[Table("tenant_data_retention_config")]
public class TenantDataRetentionConfigEntity : ITenantScoped
{
    [Key]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Number of days to retain soft-deleted records before hard-deleting.
    /// Null = use global default. Minimum enforced: 7 days.
    /// </summary>
    [Column("soft_delete_retention_days")]
    public int? SoftDeleteRetentionDays { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
