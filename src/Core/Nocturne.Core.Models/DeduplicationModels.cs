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
    /// Note text, whitespace-normalised, for note matching
    /// </summary>
    public string? Text { get; init; }

    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Input for batch deduplication — one per record being ingested.
/// </summary>
public record DeduplicationInput(Guid RecordId, long Mills, string DataSource, MatchCriteria Criteria)
{
    /// <summary>
    /// Substituted for a record that carries no data source. It names no connector, so it cannot
    /// establish that two records came from different ones and is ineligible for wide matching.
    /// </summary>
    public const string UnknownDataSource = "unknown";
}

/// <summary>
/// Stats returned from a batch deduplication call.
/// </summary>
public record DeduplicationBatchResult(int Processed, int GroupsCreated, int RecordsLinked, int DuplicateGroups);

/// <summary>
/// Result of a cursor-bounded reconcile pass: the number of duplicate groups merged
/// and whether the pass caught up to the end of the newly-created links.
/// </summary>
public record ReconcileResult(int GroupsMerged, bool CaughtUp);

public record DeduplicationProgress
{
    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; init; }

    /// <summary>
    /// Number of records processed so far
    /// </summary>
    [JsonPropertyName("processedRecords")]
    public int ProcessedRecords { get; init; }

    [JsonPropertyName("groupsFound")]
    public int GroupsFound { get; init; }

    [JsonPropertyName("recordsLinked")]
    public int RecordsLinked { get; init; }

    [JsonPropertyName("currentPhase")]
    public string CurrentPhase { get; init; } = string.Empty;

    /// <summary>
    /// Percentage complete (0-100)
    /// </summary>
    [JsonPropertyName("percentComplete")]
    public double PercentComplete =>
        TotalRecords > 0 ? (double)ProcessedRecords / TotalRecords * 100 : 0;
}

public record DeduplicationResult
{
    [JsonPropertyName("totalRecordsProcessed")]
    public int TotalRecordsProcessed { get; init; }

    [JsonPropertyName("canonicalGroupsCreated")]
    public int CanonicalGroupsCreated { get; init; }

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

    [JsonPropertyName("stateSpansProcessed")]
    public int StateSpansProcessed { get; init; }

    [JsonPropertyName("sensorGlucoseProcessed")]
    public int SensorGlucoseProcessed { get; init; }

    [JsonPropertyName("bolusesProcessed")]
    public int BolusesProcessed { get; init; }

    [JsonPropertyName("carbIntakesProcessed")]
    public int CarbIntakesProcessed { get; init; }

    [JsonPropertyName("bgChecksProcessed")]
    public int BGChecksProcessed { get; init; }

    [JsonPropertyName("deviceEventsProcessed")]
    public int DeviceEventsProcessed { get; init; }

    [JsonPropertyName("notesProcessed")]
    public int NotesProcessed { get; init; }

    [JsonPropertyName("bolusCalculationsProcessed")]
    public int BolusCalculationsProcessed { get; init; }

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

public record DeduplicationJobStatus
{
    [JsonPropertyName("jobId")]
    public Guid JobId { get; init; }

    [JsonPropertyName("state")]
    public DeduplicationJobState State { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; init; }

    /// <summary>
    /// When the job completed (if finished)
    /// </summary>
    [JsonPropertyName("completedAt")]
    public DateTime? CompletedAt { get; init; }

    [JsonPropertyName("progress")]
    public DeduplicationProgress? Progress { get; init; }

    /// <summary>
    /// Final result of the job (if completed)
    /// </summary>
    [JsonPropertyName("result")]
    public DeduplicationResult? Result { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DeduplicationJobState>))]
public enum DeduplicationJobState
{
    /// <summary>
    /// Job is queued but not yet started
    /// </summary>
    Pending,

    Running,

    Completed,

    Failed,

    Cancelled
}
