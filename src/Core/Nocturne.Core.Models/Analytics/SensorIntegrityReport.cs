namespace Nocturne.Core.Models.Analytics;

/// <summary>
/// A hypo event enriched with the service-layer nocturnal classification (the pure detector is
/// timezone-agnostic; nocturnal-window classification requires local time).
/// </summary>
public sealed record SensorIntegrityHypoEvent
{
    /// <summary>The underlying cluster-to-hypo finding from the detector.</summary>
    public required HypoEvent Event { get; init; }

    /// <summary>Whether the nadir fell within the nocturnal window (local time).</summary>
    public required bool IsNocturnal { get; init; }
}

/// <summary>Aggregate counts for a sensor-integrity analysis (overall or per data source).</summary>
public sealed record SensorIntegritySummary
{
    /// <summary>Distinct calendar days covered by the analyzed readings.</summary>
    public required int Days { get; init; }

    /// <summary>Total clusters detected.</summary>
    public required int Clusters { get; init; }

    /// <summary>Clusters rated exactly <see cref="ClusterConfidence.Medium"/>.</summary>
    public required int MediumClusters { get; init; }

    /// <summary>Clusters rated exactly <see cref="ClusterConfidence.High"/>.</summary>
    public required int HighClusters { get; init; }

    /// <summary>Cluster-linked hypo events.</summary>
    public required int Events { get; init; }

    /// <summary>Hypo events whose nadir fell in the nocturnal window.</summary>
    public required int NocturnalEvents { get; init; }
}

/// <summary>Per-data-source rollup of sensor-integrity metrics (mirrors cross-brand comparison).</summary>
public sealed record SensorIntegritySourceMetrics
{
    /// <summary>The data source / connector identifier these metrics belong to.</summary>
    public required string Source { get; init; }

    /// <summary>Aggregate counts for this source.</summary>
    public required SensorIntegritySummary Summary { get; init; }
}

/// <summary>
/// Result of a sensor-integrity analysis over a tenant's CGM data for a time window: detected
/// noise clusters, cluster-linked hypo events, and aggregate counts.
/// </summary>
public sealed record SensorIntegrityReport
{
    /// <summary>Inclusive UTC start of the analyzed window.</summary>
    public required DateTime From { get; init; }

    /// <summary>Exclusive UTC end of the analyzed window.</summary>
    public required DateTime To { get; init; }

    /// <summary>Data source filter applied, or <c>null</c> for the combined deduplicated stream.</summary>
    public string? Source { get; init; }

    /// <summary>All detected clusters, ordered by start time.</summary>
    public required IReadOnlyList<GlucoseCluster> Clusters { get; init; }

    /// <summary>Cluster-linked hypo events, with nocturnal classification.</summary>
    public required IReadOnlyList<SensorIntegrityHypoEvent> HypoEvents { get; init; }

    /// <summary>Overall aggregate counts.</summary>
    public required SensorIntegritySummary Summary { get; init; }

    /// <summary>Per-source breakdown when requested; otherwise <c>null</c>.</summary>
    public IReadOnlyList<SensorIntegritySourceMetrics>? PerSource { get; init; }
}
