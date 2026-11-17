namespace Nocturne.Core.Models.V4;

/// <summary>
/// Aggregated status of the transition from the legacy Nightscout v1/v3 API to V4.
/// Returned by the admin transition-status endpoint to help operators decide when it is safe
/// to disconnect the legacy write-back proxy.
/// </summary>
/// <seealso cref="MigrationStatusInfo"/>
/// <seealso cref="WriteBackHealthInfo"/>
/// <seealso cref="CompatibilityInfo"/>
/// <seealso cref="DisconnectRecommendation"/>
public class NightscoutTransitionStatus
{
    /// <summary>
    /// Current state of the V4 data migration from legacy records.
    /// </summary>
    public MigrationStatusInfo Migration { get; set; } = new();

    /// <summary>
    /// Health metrics for the legacy write-back proxy over the last 24 hours.
    /// </summary>
    public WriteBackHealthInfo WriteBack { get; set; } = new();

    /// <summary>
    /// Compatibility comparison results between the legacy and V4 APIs (null if not yet computed).
    /// </summary>
    public CompatibilityInfo? Compatibility { get; set; }

    /// <summary>
    /// Operator recommendation for whether it is safe to disable the legacy proxy.
    /// </summary>
    public DisconnectRecommendation Recommendation { get; set; } = new();
}

/// <summary>
/// Compatibility score derived by comparing responses from the legacy and V4 APIs.
/// </summary>
public class CompatibilityInfo
{
    /// <summary>
    /// Whether the legacy write-back proxy is currently enabled.
    /// </summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>
    /// Percentage (0–1) of API response fields that matched between legacy and V4 (null if not computed).
    /// </summary>
    public double? CompatibilityScore { get; set; }

    public int TotalComparisons { get; set; }

    /// <summary>
    /// Number of fields that differed between legacy and V4 responses.
    /// </summary>
    public int Discrepancies { get; set; }
}

/// <summary>
/// Snapshot of V4 migration progress: per-type record counts and last sync timestamp.
/// </summary>
public class MigrationStatusInfo
{
    /// <summary>
    /// Per-record-type counts of migrated V4 records (keyed by type name, e.g., "SensorGlucose").
    /// </summary>
    public Dictionary<string, int> RecordCounts { get; set; } = new();

    /// <summary>
    /// When the migration last successfully synced records (null if migration has not started).
    /// </summary>
    public DateTimeOffset? LastSyncTime { get; set; }

    /// <summary>
    /// Whether the migration has completed processing all legacy records.
    /// </summary>
    public bool IsComplete { get; set; }
}

/// <summary>
/// Health metrics for the legacy Nightscout write-back proxy over a rolling 24-hour window.
/// </summary>
public class WriteBackHealthInfo
{
    /// <summary>
    /// Total write-back requests made to the legacy API in the last 24 hours.
    /// </summary>
    public int RequestsLast24h { get; set; }

    /// <summary>
    /// Successful write-back requests in the last 24 hours.
    /// </summary>
    public int SuccessesLast24h { get; set; }

    /// <summary>
    /// Failed write-back requests in the last 24 hours.
    /// </summary>
    public int FailuresLast24h { get; set; }

    /// <summary>
    /// Whether the circuit breaker is currently open (proxy is pausing requests due to repeated failures).
    /// </summary>
    public bool CircuitBreakerOpen { get; set; }

    /// <summary>
    /// Timestamp of the most recent successful write-back (null if none in the window).
    /// </summary>
    public DateTimeOffset? LastSuccessTime { get; set; }
}

/// <summary>
/// Recommendation on whether it is safe to disconnect the legacy Nightscout write-back proxy.
/// </summary>
public class DisconnectRecommendation
{
    /// <summary>
    /// Readiness status: <c>"not-ready"</c>, <c>"almost-ready"</c>, or <c>"safe"</c>.
    /// </summary>
    public string Status { get; set; } = "not-ready";

    /// <summary>
    /// Human-readable reasons why the proxy cannot yet be safely disconnected.
    /// Empty when <see cref="Status"/> is <c>"safe"</c>.
    /// </summary>
    public List<string> Blockers { get; set; } = [];

    /// <summary>
    /// How many more days of stability are required before a safe disconnect can be recommended (null if already safe).
    /// </summary>
    public int? StabilityDaysRemaining { get; set; }
}
