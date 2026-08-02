using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// PostgreSQL entity for tracking migration sources
/// Stores information about Nightscout instances or MongoDB databases used for migration
/// </summary>
[Table("migration_sources")]
public class MigrationSourceEntity
{
    /// <summary>
    /// Primary key - UUID Version 7 for time-ordered, globally unique identification
    /// </summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The tenant that migrated from this source. Sources dedupe per tenant, and the
    /// tenant-facing sources listing filters on this — a source URL frequently identifies
    /// a person, so one tenant must never see another tenant's sources.
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
    /// Unique identifier for the source - URL (API mode) or connection string hash (MongoDB)
    /// Used for deduplication to prevent duplicate sources
    /// </summary>
    [Column("source_identifier")]
    [MaxLength(255)]
    public string SourceIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// Nightscout URL for API mode
    /// </summary>
    [Column("nightscout_url")]
    [MaxLength(512)]
    public string? NightscoutUrl { get; set; }

    /// <summary>
    /// Hashed API secret for API mode (never stored in plain text)
    /// </summary>
    [Column("nightscout_api_secret_hash")]
    [MaxLength(128)]
    public string? NightscoutApiSecretHash { get; set; }

    /// <summary>
    /// Encrypted MongoDB connection string for MongoDB mode (contains credentials)
    /// </summary>
    [Column("mongo_connection_string_encrypted")]
    public string? MongoConnectionStringEncrypted { get; set; }

    /// <summary>
    /// MongoDB database name for MongoDB mode
    /// </summary>
    [Column("mongo_database_name")]
    [MaxLength(255)]
    public string? MongoDatabaseName { get; set; }

    /// <summary>
    /// When the last successful migration completed
    /// </summary>
    [Column("last_migration_at")]
    public DateTime? LastMigrationAt { get; set; }

    /// <summary>
    /// Newest data timestamp migrated (for "since last" default in UI)
    /// </summary>
    [Column("last_migrated_data_timestamp")]
    public DateTime? LastMigratedDataTimestamp { get; set; }

    /// <summary>
    /// When this source was first added
    /// </summary>
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation property for migration runs
    /// </summary>
    public ICollection<MigrationRunEntity> Runs { get; set; } = [];
}
