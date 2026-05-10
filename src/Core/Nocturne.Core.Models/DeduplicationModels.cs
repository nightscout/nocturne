using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// Criteria for matching records during deduplication.
/// Fields are selective: populate only those relevant to the record type being matched
/// (<see cref="Entry"/>, <see cref="Treatment"/>, or <see cref="StateSpan"/>).
/// </summary>
/// <seealso cref="DeduplicationResult"/>
/// <seealso cref="DeduplicationJobStatus"/>
public record MatchCriteria
{
    // Entry matching
    /// <summary>
    /// Glucose value for entry matching (Sgv or Mbg)
    /// </summary>
    public double? GlucoseValue { get; init; }

    /// <summary>
    /// Tolerance for glucose value matching (default ±5 mg/dL)
    /// </summary>
    public double GlucoseTolerance { get; init; } = 5.0;

    /// <summary>
    /// Entry type filter (sgv, mbg, cal)
    /// </summary>
    public string? EntryType { get; init; }

    // Treatment matching
    /// <summary>
    /// Event type for treatment matching
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Insulin amount for bolus matching
    /// </summary>
    public double? Insulin { get; init; }

    /// <summary>
    /// Carbs amount for meal/carb treatment matching
    /// </summary>
    public double? Carbs { get; init; }

    /// <summary>
    /// Tolerance for insulin matching (default ±0.05 units)
    /// </summary>
    public double InsulinTolerance { get; init; } = 0.05;

    /// <summary>
    /// Tolerance for carbs matching (default ±1g)
    /// </summary>
    public double CarbsTolerance { get; init; } = 1.0;

    // StateSpan matching
    /// <summary>
    /// State span category for matching
    /// </summary>
    public StateSpanCategory? Category { get; init; }

    /// <summary>
    /// State value for state span matching
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// End timestamp for state span matching
    /// </summary>
    public long? EndMills { get; init; }

    // Shared fields
    /// <summary>
    /// Rate for temp basal matching
    /// </summary>
    public double? Rate { get; init; }

    /// <summary>
    /// Duration for temp basal or state span matching
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Tolerance for duration matching (default ±1 minute)
    /// </summary>
    public TimeSpan DurationTolerance { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Tolerance for rate matching (default ±0.05 u/hr)
    /// </summary>
    public double RateTolerance { get; init; } = 0.05;

    /// <summary>
    /// Metadata for additional matching criteria
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Input for batch deduplication — one per record being ingested.
/// </summary>
public record DeduplicationInput(Guid RecordId, long Mills, string DataSource, MatchCriteria Criteria);

/// <summary>
/// Stats returned from a batch deduplication call.
/// </summary>
public record DeduplicationBatchResult(int Processed, int GroupsCreated, int RecordsLinked, int DuplicateGroups);

/// <summary>
/// Progress information for deduplication job
/// </summary>
public record DeduplicationProgress
{
    /// <summary>
    /// Total number of records to process
    /// </summary>
    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; init; }

    /// <summary>
    /// Number of records processed so far
    /// </summary>
    [JsonPropertyName("processedRecords")]
    public int ProcessedRecords { get; init; }

    /// <summary>
    /// Number of canonical groups found
    /// </summary>
    [JsonPropertyName("groupsFound")]
    public int GroupsFound { get; init; }

    /// <summary>
    /// Number of records linked to groups
    /// </summary>
    [JsonPropertyName("recordsLinked")]
    public int RecordsLinked { get; init; }

    /// <summary>
    /// Current processing phase
    /// </summary>
    [JsonPropertyName("currentPhase")]
    public string CurrentPhase { get; init; } = string.Empty;

    /// <summary>
    /// Percentage complete (0-100)
    /// </summary>
    [JsonPropertyName("percentComplete")]
    public double PercentComplete =>
        TotalRecords > 0 ? (double)ProcessedRecords / TotalRecords * 100 : 0;
}

/// <summary>
/// Result of a deduplication job
/// </summary>
public record DeduplicationResult
{
    /// <summary>
    /// Total number of records processed
    /// </summary>
    [JsonPropertyName("totalRecordsProcessed")]
    public int TotalRecordsProcessed { get; init; }

    /// <summary>
    /// Number of canonical groups created
    /// </summary>
    [JsonPropertyName("canonicalGroupsCreated")]
    public int CanonicalGroupsCreated { get; init; }

    /// <summary>
    /// Number of records linked to groups
    /// </summary>
    [JsonPropertyName("recordsLinked")]
    public int RecordsLinked { get; init; }

    /// <summary>
    /// Number of duplicate groups found (groups with more than one record)
    /// </summary>
    [JsonPropertyName("duplicateGroupsFound")]
    public int DuplicateGroupsFound { get; init; }

    /// <summary>
    /// Time taken to complete the job
    /// </summary>
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Number of entries processed
    /// </summary>
    [JsonPropertyName("entriesProcessed")]
    public int EntriesProcessed { get; init; }

    /// <summary>
    /// Number of treatments processed
    /// </summary>
    [JsonPropertyName("treatmentsProcessed")]
    public int TreatmentsProcessed { get; init; }

    /// <summary>
    /// Number of state spans processed
    /// </summary>
    [JsonPropertyName("stateSpansProcessed")]
    public int StateSpansProcessed { get; init; }

    /// <summary>
    /// Number of sensor glucose records processed
    /// </summary>
    [JsonPropertyName("sensorGlucoseProcessed")]
    public int SensorGlucoseProcessed { get; init; }

    /// <summary>
    /// Number of bolus records processed
    /// </summary>
    [JsonPropertyName("bolusesProcessed")]
    public int BolusesProcessed { get; init; }

    /// <summary>
    /// Number of carb intake records processed
    /// </summary>
    [JsonPropertyName("carbIntakesProcessed")]
    public int CarbIntakesProcessed { get; init; }

    /// <summary>
    /// Number of BG check records processed
    /// </summary>
    [JsonPropertyName("bgChecksProcessed")]
    public int BGChecksProcessed { get; init; }

    /// <summary>
    /// Number of device event records processed
    /// </summary>
    [JsonPropertyName("deviceEventsProcessed")]
    public int DeviceEventsProcessed { get; init; }

    /// <summary>
    /// Number of note records processed
    /// </summary>
    [JsonPropertyName("notesProcessed")]
    public int NotesProcessed { get; init; }

    /// <summary>
    /// Number of bolus calculation records processed
    /// </summary>
    [JsonPropertyName("bolusCalculationsProcessed")]
    public int BolusCalculationsProcessed { get; init; }

    /// <summary>
    /// Number of temp basal records processed
    /// </summary>
    [JsonPropertyName("tempBasalsProcessed")]
    public int TempBasalsProcessed { get; init; }

    /// <summary>
    /// Whether the job completed successfully
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the job failed
    /// </summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Status of a running deduplication job
/// </summary>
public record DeduplicationJobStatus
{
    /// <summary>
    /// Unique identifier for the job
    /// </summary>
    [JsonPropertyName("jobId")]
    public Guid JobId { get; init; }

    /// <summary>
    /// Current state of the job
    /// </summary>
    [JsonPropertyName("state")]
    public DeduplicationJobState State { get; init; }

    /// <summary>
    /// When the job was started
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// When the job completed (if finished)
    /// </summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Current progress of the job
    /// </summary>
    [JsonPropertyName("progress")]
    public DeduplicationProgress? Progress { get; init; }

    /// <summary>
    /// Final result of the job (if completed)
    /// </summary>
    [JsonPropertyName("result")]
    public DeduplicationResult? Result { get; init; }
}

/// <summary>
/// State of a deduplication job
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeduplicationJobState>))]
public enum DeduplicationJobState
{
    /// <summary>
    /// Job is queued but not yet started
    /// </summary>
    Pending,

    /// <summary>
    /// Job is currently running
    /// </summary>
    Running,

    /// <summary>
    /// Job completed successfully
    /// </summary>
    Completed,

    /// <summary>
    /// Job failed with an error
    /// </summary>
    Failed,

    /// <summary>
    /// Job was cancelled
    /// </summary>
    Cancelled
}
