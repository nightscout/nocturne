using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity recording a platform-admin connector cursor reset job. The job itself runs on
/// a detached in-process task and cannot survive an API restart, but its record must: without a
/// persisted row, a restart mid-run leaves the operator polling a 404 with no way to tell "job id
/// never existed" from "the work was killed partway". Rows are written at every lifecycle
/// transition; startup marks any row still Pending/Running as Interrupted.
/// </summary>
/// <remarks>
/// Operator metadata, not tenant data: rows carry a <see cref="TenantId"/> for filtering but the
/// table is deliberately not tenant-scoped (no RLS) — it is read cross-tenant by platform-admin
/// endpoints and contains job lifecycle state only, no health data.
/// </remarks>
[Table("connector_reset_jobs")]
public class ConnectorResetJobEntity
{
    /// <summary>Primary key — matches the reset job id handed to the polling client.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The tenant whose connectors the job resets.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The target tenant's slug, denormalized for display in job listings.</summary>
    [Column("tenant_slug")]
    [MaxLength(64)]
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>Current state: Pending, Running, Completed, Failed, Cancelled, Interrupted.</summary>
    [Column("state")]
    [MaxLength(20)]
    public string State { get; set; } = "Pending";

    /// <summary>When the job was created.</summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>When the background work started, or null if it never got that far.</summary>
    [Column("started_at")]
    public DateTime? StartedAt { get; set; }

    /// <summary>When the job reached a terminal state.</summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>Error message when the whole job failed (not a single connector).</summary>
    [Column("error_message")]
    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }

    /// <summary>Total connectors the job set out to reset.</summary>
    [Column("total_connectors")]
    public int TotalConnectors { get; set; }

    /// <summary>How many connectors reached a terminal state.</summary>
    [Column("completed_connectors")]
    public int CompletedConnectors { get; set; }

    /// <summary>Serialized per-connector progress snapshot (the status endpoint's Connectors list).</summary>
    [Column("connectors_json", TypeName = "jsonb")]
    public string? ConnectorsJson { get; set; }
}
