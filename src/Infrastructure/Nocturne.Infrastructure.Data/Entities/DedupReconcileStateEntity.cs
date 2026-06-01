using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity recording how far dedup reconciliation has processed for a tenant.
/// One row per tenant, keyed on the ingestion time (<c>linked_records.sys_created_at</c>)
/// of the last reconciled link.
/// </summary>
[Table("dedup_reconcile_state")]
public class DedupReconcileStateEntity : ITenantScoped
{
    /// <summary>
    /// The unique identifier of the tenant this state belongs to. Primary key — one row per tenant.
    /// </summary>
    [Key]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Ingestion time (<c>linked_records.sys_created_at</c>) of the last reconciled link.
    /// Reconciliation resumes from records created after this point.
    /// </summary>
    [Column("last_reconciled_link_created_at")]
    public DateTime LastReconciledLinkCreatedAt { get; set; }
}
