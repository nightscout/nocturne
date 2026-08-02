namespace Nocturne.API.Services.Migration;

/// <summary>
/// Enumerates the modes for data migration
/// </summary>
public enum MigrationMode
{
    /// <summary>
    /// Migrate data via Nightscout REST API
    /// </summary>
    Api,

    /// <summary>
    /// Migrate data directly from MongoDB
    /// </summary>
    MongoDb
}

/// <summary>
/// Lifecycle state of a <see cref="MigrationJobInfo"/>.
/// Transitions are: Pending → Validating → Running → Completed|Failed, or any state → Cancelled.
/// </summary>
public enum MigrationJobState
{
    /// <summary>Job has been created and is waiting to start.</summary>
    Pending,
    /// <summary>Connection to the migration source is being tested before data transfer begins.</summary>
    Validating,
    /// <summary>Data is actively being transferred.</summary>
    Running,
    /// <summary>All requested collections have been migrated successfully.</summary>
    Completed,
    /// <summary>Migration terminated due to an unrecoverable error. See <see cref="MigrationJobInfo.ErrorMessage"/>.</summary>
    Failed,
    /// <summary>Migration was explicitly stopped by the user before completion.</summary>
    Cancelled,
    /// <summary>The API restarted while the migration was running; the work did not complete and must be re-run.</summary>
    Interrupted
}

/// <summary>
/// Parameters for starting a new data migration from Nightscout or MongoDB.
/// </summary>
public record StartMigrationRequest
{
    /// <summary>
    /// Migration mode (API or MongoDB)
    /// </summary>
    public MigrationMode Mode { get; init; }

    /// <summary>
    /// Nightscout URL (for API mode)
    /// </summary>
    public string? NightscoutUrl { get; init; }

    /// <summary>
    /// Nightscout API secret (for API mode)
    /// </summary>
    public string? NightscoutApiSecret { get; init; }

    /// <summary>
    /// MongoDB connection string (for MongoDB mode)
    /// </summary>
    public string? MongoConnectionString { get; init; }

    /// <summary>
    /// MongoDB database name (for MongoDB mode)
    /// </summary>
    public string? MongoDatabaseName { get; init; }

    /// <summary>
    /// Collections to migrate. Empty means all.
    /// </summary>
    public List<string> Collections { get; init; } = [];

    /// <summary>
    /// Start date for migration (optional)
    /// </summary>
    public DateTime? StartDate { get; init; }

    /// <summary>
    /// End date for migration (optional)
    /// </summary>
    public DateTime? EndDate { get; init; }
}

/// <summary>
/// Information about a migration job
/// </summary>
public record MigrationJobInfo
{
    public required Guid Id { get; init; }
    public required MigrationMode Mode { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? SourceDescription { get; init; }
    public MigrationJobState State { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}


/// <summary>
/// Real-time progress snapshot for a running <see cref="MigrationJobInfo"/>.
/// <see cref="EstimatedTimeRemaining"/> is <see langword="null"/> until enough records have
/// been processed to project a completion time.
/// </summary>
public record MigrationJobStatus
{
    public required Guid JobId { get; init; }
    public required MigrationJobState State { get; init; }
    public required double ProgressPercentage { get; init; }
    public string? CurrentOperation { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public Dictionary<string, CollectionProgress> CollectionProgress { get; init; } = [];
}

/// <summary>
/// Progress for a specific collection
/// </summary>
public record CollectionProgress
{
    public required string CollectionName { get; init; }
    public long TotalDocuments { get; init; }
    public long DocumentsMigrated { get; init; }
    public long DocumentsFailed { get; init; }
    public bool IsComplete { get; init; }
}

/// <summary>
/// Request to test a Nightscout connection
/// </summary>
public record TestMigrationConnectionRequest
{
    public MigrationMode Mode { get; init; }
    public string? NightscoutUrl { get; init; }
    public string? NightscoutApiSecret { get; init; }
    public string? MongoConnectionString { get; init; }
    public string? MongoDatabaseName { get; init; }
}

/// <summary>
/// Result of testing a migration connection
/// </summary>
public record TestMigrationConnectionResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SiteName { get; init; }
    public string? Version { get; init; }
    public long? EntryCount { get; init; }
    public long? TreatmentCount { get; init; }
    public List<string> AvailableCollections { get; init; } = [];
}

/// <summary>
/// Auto-start migration configuration discovered from environment variables
/// (<c>MIGRATION_MODE</c>, <c>MIGRATION_NS_URL</c>, <c>MIGRATION_NS_API_SECRET</c>, etc.).
/// Credentials are never returned verbatim; only their presence is indicated via
/// <see cref="HasApiSecret"/> and <see cref="HasMongoConnectionString"/>.
/// </summary>
public record PendingMigrationConfig
{
    /// <summary>
    /// Whether there is a pending migration configuration in env vars
    /// </summary>
    public bool HasPendingConfig { get; init; }

    /// <summary>
    /// Migration mode from MIGRATION_MODE env var
    /// </summary>
    public MigrationMode? Mode { get; init; }

    /// <summary>
    /// Nightscout URL from MIGRATION_NS_URL env var
    /// </summary>
    public string? NightscoutUrl { get; init; }

    /// <summary>
    /// Whether MIGRATION_NS_API_SECRET is set (never returns the actual secret)
    /// </summary>
    public bool HasApiSecret { get; init; }

    /// <summary>
    /// Whether MIGRATION_MONGO_CONNECTION_STRING is set (never returns the actual string)
    /// </summary>
    public bool HasMongoConnectionString { get; init; }

    /// <summary>
    /// MongoDB database name from MIGRATION_MONGO_DATABASE_NAME env var
    /// </summary>
    public string? MongoDatabaseName { get; init; }
}

/// <summary>
/// Migration source DTO for API responses
/// </summary>
public record MigrationSourceDto
{
    /// <summary>
    /// Unique identifier for this source
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Migration mode (Api or MongoDb)
    /// </summary>
    public required MigrationMode Mode { get; init; }

    /// <summary>
    /// Nightscout URL (for API mode)
    /// </summary>
    public string? NightscoutUrl { get; init; }

    /// <summary>
    /// MongoDB database name (for MongoDB mode)
    /// </summary>
    public string? MongoDatabaseName { get; init; }

    /// <summary>
    /// When the last successful migration completed
    /// </summary>
    public DateTime? LastMigrationAt { get; init; }

    /// <summary>
    /// Newest data timestamp migrated (for "since last" default)
    /// </summary>
    public DateTime? LastMigratedDataTimestamp { get; init; }

    /// <summary>
    /// When this source was first added
    /// </summary>
    public DateTime CreatedAt { get; init; }
}

