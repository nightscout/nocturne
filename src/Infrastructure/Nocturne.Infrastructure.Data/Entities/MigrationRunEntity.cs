using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for tracking individual migration runs
/// Records the history of migration jobs with their results
/// </summary>
[Table("migration_runs")]
public class MigrationRunEntity
{
    /// <summary>
    /// Primary key - matches the migration job ID
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Foreign key to the migration source
    /// </summary>
    [Column("source_id")]
    public Guid SourceId { get; set; }

    /// <summary>
    /// The tenant that started (and owns) this migration. Used to scope history and
    /// status lookups; the table itself is operator metadata and not RLS-scoped.
    /// </summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Migration mode: "Api" or "MongoDb"
    /// </summary>
    [Column("mode")]
    [MaxLength(20)]
    public string Mode { get; set; } = "Api";

    /// <summary>
    /// Human-readable source description shown in job history (URL or database name)
    /// </summary>
    [Column("source_description")]
    [MaxLength(512)]
    public string? SourceDescription { get; set; }

    /// <summary>
    /// When the migration job was created
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the migration job started
    /// </summary>
    [Column("started_at")]
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the migration job completed (null if still running)
    /// </summary>
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Current state: Pending, Running, Completed, Failed, Cancelled, Interrupted
    /// </summary>
    [Column("state")]
    [MaxLength(20)]
    public string State { get; set; } = "Pending";

    /// <summary>
    /// User-specified start date for the migration range
    /// </summary>
    [Column("date_range_start")]
    public DateTime? DateRangeStart { get; set; }

    /// <summary>
    /// User-specified end date for the migration range
    /// </summary>
    [Column("date_range_end")]
    public DateTime? DateRangeEnd { get; set; }

    /// <summary>
    /// Number of entries successfully migrated
    /// </summary>
    [Column("entries_migrated")]
    public int EntriesMigrated { get; set; }

    /// <summary>
    /// Number of treatments successfully migrated
    /// </summary>
    [Column("treatments_migrated")]
    public int TreatmentsMigrated { get; set; }

    /// <summary>
    /// Error message if the migration failed
    /// </summary>
    [Column("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Navigation property for the migration source
    /// </summary>
    [ForeignKey(nameof(SourceId))]
    public MigrationSourceEntity Source { get; set; } = null!;
}
