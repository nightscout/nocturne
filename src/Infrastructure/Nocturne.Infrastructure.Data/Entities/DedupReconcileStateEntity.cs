using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity recording how far dedup reconciliation has processed for a tenant.
/// One row per tenant, holding the last reconciled link as a
/// (<c>sys_created_at</c>, <c>id</c>) keyset cursor.
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
    /// </summary>
    [Column("last_reconciled_link_created_at")]
    public DateTime LastReconciledLinkCreatedAt { get; set; }

    /// <summary>
    /// Id of the last reconciled link, ordering the links that share
    /// <see cref="LastReconciledLinkCreatedAt"/>. Null on rows written before the column existed;
    /// reconciliation then resumes at the first link of that instant.
    /// </summary>
    [Column("last_reconciled_link_id")]
    public Guid? LastReconciledLinkId { get; set; }
}
