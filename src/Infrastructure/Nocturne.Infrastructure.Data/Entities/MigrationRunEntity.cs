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
    /// Human-readable source description shown in job history: the source's
    /// <see cref="MigrationSourceEntity.NightscoutUrl"/>, or the database name
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
    /// Per-collection outcome for the run: a JSON array of
    /// <c>{ collectionName, totalDocuments, documentsMigrated, documentsFailed, isComplete, failureReason, skippedReason }</c>.
    /// The two count columns above cover entries and treatments only.
    /// </summary>
    [Column("collection_outcomes", TypeName = "jsonb")]
    public string? CollectionOutcomes { get; set; }

    /// <summary>
    /// The source's alarm thresholds and switches as read when an API-mode run began, as JSON; null
    /// when the run read none. The setup hub starts the Alerts item from them.
    /// </summary>
    [Column("source_alarm_settings", TypeName = "jsonb")]
    public string? SourceAlarmSettings { get; set; }

    /// <summary>
    /// Where a live run resumes and what it has counted so far, as the importer's own JSON. Written
    /// before each page a run reads, so a run whose process died goes on from the page it was on.
    /// </summary>
    [Column("checkpoint", TypeName = "jsonb")]
    public string? Checkpoint { get; set; }

    /// <summary>
    /// The run's request, source credentials included, encrypted with the instance key. Held only
    /// while the run is live, for the process that resumes it; null once the run has ended.
    /// </summary>
    [Column("request_encrypted")]
    public string? RequestEncrypted { get; set; }

    /// <summary>
    /// Counts the writes to this row. A holder writes only over the revision it last wrote, so one
    /// that lost the run to another process cannot put an older checkpoint back.
    /// </summary>
    [Column("revision")]
    public int Revision { get; set; }

    /// <summary>
    /// The subject who started the run, stamped as the maker of every token it adopts; null for a run
    /// started before runs recorded one, which adopts with no maker.
    /// </summary>
    [Column("started_by_subject_id")]
    public Guid? StartedBySubjectId { get; set; }

    /// <summary>
    /// When the starter's access ends, read once at start and the expiry of every token the run
    /// adopts. A later end only lowers it (<see cref="Repositories.MemberCreations.CapAsync"/>).
    /// </summary>
    [Column("starter_access_ends_at")]
    public DateTime? StarterAccessEndsAt { get; set; }

    /// <summary>
    /// Navigation property for the migration source
    /// </summary>
    [ForeignKey(nameof(SourceId))]
    public MigrationSourceEntity Source { get; set; } = null!;
}
