using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity recording how far dedup reconciliation and the singleton prune have processed
/// for a tenant, each as a (<c>sys_created_at</c>, <c>id</c>) keyset cursor, and the state of its full
/// deduplication run. One row per tenant.
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

    /// <summary>
    /// Ingestion time of the last primary link the singleton prune examined. Null before the
    /// prune's first page, and after a restart that found no links.
    /// </summary>
    [Column("prune_cursor_created_at")]
    public DateTime? PruneCursorCreatedAt { get; set; }

    /// <summary>Id of the last primary link the singleton prune examined.</summary>
    [Column("prune_cursor_id")]
    public Guid? PruneCursorId { get; set; }

    /// <summary>
    /// When the singleton prune last restarted from the tenant's first link. Null until its first pass.
    /// </summary>
    [Column("prune_cursor_reset_at")]
    public DateTime? PruneCursorResetAt { get; set; }

    /// <summary>
    /// The tenant's latest full deduplication run as JSON: its status, running totals and the totals
    /// at its last resume point (<see cref="Services.Deduplication.DeduplicationAdmin"/>). Null until a
    /// run is first requested.
    /// </summary>
    [Column("run", TypeName = "jsonb")]
    public string? Run { get; set; }

    /// <summary>When a cancel of the running full run was asked for; null when none is waiting.</summary>
    [Column("run_cancel_requested_at")]
    public DateTime? RunCancelRequestedAt { get; set; }
}
