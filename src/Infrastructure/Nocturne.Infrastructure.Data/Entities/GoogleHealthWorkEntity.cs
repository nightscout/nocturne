using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// One row per tenant: its Google Health work in flight
/// (<see cref="Core.Contracts.Health.IGoogleHealthWorkStore"/>). A tenant has at most one Google
/// Health connection, the <c>googlehealth</c> row of <c>connector_configurations</c>. The row stays
/// once written, with both parts cleared when there is no work; it goes with its tenant.
/// </summary>
[Table("google_health_work")]
public class GoogleHealthWorkEntity : ITenantScoped
{
    [Key]
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The import's phase as its JSON name (<c>queued</c>, <c>reading</c>, ...); null when no import is in flight.</summary>
    [Column("progress_phase")]
    public string? ProgressPhase { get; set; }

    [Column("progress_data_type")]
    public string? ProgressDataType { get; set; }

    [Column("progress_completed_data_types")]
    public int ProgressCompletedDataTypes { get; set; }

    [Column("progress_total_data_types")]
    public int ProgressTotalDataTypes { get; set; }

    [Column("progress_pages_read")]
    public int ProgressPagesRead { get; set; }

    [Column("progress_worker_owned")]
    public bool ProgressWorkerOwned { get; set; }

    /// <summary>The pending disconnect's id; null when none is pending.</summary>
    [Column("disconnect_request_id")]
    public Guid? DisconnectRequestId { get; set; }

    [Column("disconnect_subject_id")]
    public Guid? DisconnectSubjectId { get; set; }
}
