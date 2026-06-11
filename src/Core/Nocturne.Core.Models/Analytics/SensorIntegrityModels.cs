using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.Analytics;

/// <summary>
/// Confidence that a detected cluster represents physiologically implausible (noisy) CGM data
/// rather than genuine glycemic movement.
/// </summary>
/// <remarks>
/// Ordinal ordering matters: promotion steps move a cluster up one level
/// (<see cref="Low"/> → <see cref="Medium"/> → <see cref="High"/>).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ClusterConfidence>))]
public enum ClusterConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// Tunable parameters for <see cref="SensorIntegrityDetector"/>. Defaults mirror the published
/// device-agnostic profile of the reference v6 detector (cgm_cluster_detector_v5).
/// </summary>
/// <remarks>
/// All glucose thresholds are expressed in mg/dL, matching <see cref="V4.SensorGlucose.Mgdl"/>
/// (the source-of-truth unit), so no conversion is applied.
/// </remarks>
public sealed record DetectorConfig
{
    /// <summary>Nominal analysis window length in minutes (≈ rolling window width).</summary>
    public double WindowMinutes { get; init; } = 30.0;

    /// <summary>
    /// Time-based floor for the rolling window. Expressed in minutes (not points) so that coarse
    /// sampling rates (e.g. 15-minute Libre history) do not silently inflate the window.
    /// </summary>
    public double MinWindowMinutes { get; init; } = 15.0;

    /// <summary>Minimum directional reversals within a window for it to qualify as incoherent.</summary>
    public int MinReversals { get; init; } = 2;

    /// <summary>Minimum peak-to-trough amplitude (mg/dL) within a window.</summary>
    public double MinAmp { get; init; } = 15.0;

    /// <summary>
    /// Minimum incoherence ratio: extra path length beyond net displacement, as a fraction of total
    /// path length. 0 = monotonic, approaching 1 = pure oscillation.
    /// </summary>
    public double IncoherenceRatioThreshold { get; init; } = 0.35;

    /// <summary>Clusters shorter than this duration (minutes) are discarded.</summary>
    public double MinClusterMinutes { get; init; } = 20.0;

    /// <summary>
    /// Minimum reading gap (minutes) that masks any window spanning it. The effective threshold is
    /// <c>max(this, 2.5 × median sampling interval)</c> so it scales with sampling rate. Prevents
    /// false clusters across sensor swaps, signal loss, and DST transitions.
    /// </summary>
    public double GapThresholdMinutes { get; init; } = 12.0;

    /// <summary>Score at or above which a cluster is rated <see cref="ClusterConfidence.High"/>.</summary>
    public double ConfHigh { get; init; } = 3.0;

    /// <summary>Score at or above which a cluster is rated <see cref="ClusterConfidence.Medium"/>.</summary>
    public double ConfMedium { get; init; } = 2.0;

    /// <summary>Maximum inter-cluster gap (minutes) for two clusters to belong to the same chain.</summary>
    public double ChainRadiusMinutes { get; init; } = 30.0;

    /// <summary>Chains of at least this many clusters have all members promoted one confidence level.</summary>
    public int ChainMinSize { get; init; } = 3;

    /// <summary>A single step (mg/dL) at or above this promotes the cluster one confidence level.</summary>
    public double SpikeBumpDeltaMgdl { get; init; } = 40.0;

    /// <summary>Whether single-step spike promotion is enabled.</summary>
    public bool SpikeBumpEnabled { get; init; } = true;

    /// <summary>
    /// Grace window (minutes) around a calibration timestamp within which spike promotion is
    /// suppressed (a calibration legitimately produces a large single step).
    /// </summary>
    public double CalibrationGraceMinutes { get; init; } = 15.0;

    /// <summary>Known calibration timestamps used to suppress spike promotion near calibrations.</summary>
    public IReadOnlyList<DateTime> CalibrationTimestamps { get; init; } = [];
}

/// <summary>
/// Per-cluster diagnostic detail mirroring the reference detector's <c>debug</c> payload. Surfaced
/// for explainability (why a window was flagged) and for golden-vector parity testing.
/// </summary>
public sealed record ClusterDiagnostics
{
    /// <summary>Median sampling interval (minutes) estimated for the series.</summary>
    public required double SamplingIntervalMinutes { get; init; }

    /// <summary>Rolling window size in points derived from the sampling interval.</summary>
    public required int WindowPoints { get; init; }

    /// <summary>Maximum reversal count observed across the cluster's windows.</summary>
    public required double PeakReversals { get; init; }

    /// <summary>Maximum incoherence ratio observed across the cluster's windows.</summary>
    public required double PeakIncoherenceRatio { get; init; }

    /// <summary>Peak-to-trough amplitude (mg/dL) over the cluster span.</summary>
    public required double Amplitude { get; init; }

    /// <summary>Largest single-step change (mg/dL) within the cluster span.</summary>
    public required double MaxStep { get; init; }

    /// <summary>Whether the cluster was promoted by single-step spike detection.</summary>
    public required bool SpikePromoted { get; init; }

    /// <summary>Number of clusters in this cluster's chain (connected component).</summary>
    public required int ChainSize { get; init; }

    /// <summary>Whether the cluster was promoted by chain-density detection.</summary>
    public required bool ChainPromoted { get; init; }
}

/// <summary>
/// A contiguous window of directionally-incoherent CGM data that is unlikely to be physiologic and
/// could mislead dosing decisions.
/// </summary>
/// <remarks>
/// This is a post-hoc finding: <see cref="Start"/> is back-dated once subsequent readings make the
/// oscillation pattern evident. It is not a real-time signal.
/// </remarks>
public sealed record GlucoseCluster
{
    /// <summary>UTC start of the cluster (back-dated by the analysis window).</summary>
    public required DateTime Start { get; init; }

    /// <summary>UTC end of the cluster.</summary>
    public required DateTime End { get; init; }

    /// <summary>Minimum glucose (mg/dL) within the cluster span.</summary>
    public required double MinMgdl { get; init; }

    /// <summary>Maximum glucose (mg/dL) within the cluster span.</summary>
    public required double MaxMgdl { get; init; }

    /// <summary>Cluster duration in minutes.</summary>
    public required double DurationMinutes { get; init; }

    /// <summary>Confidence that the cluster represents noise.</summary>
    public required ClusterConfidence Confidence { get; init; }

    /// <summary>Diagnostic detail behind the detection and scoring.</summary>
    public required ClusterDiagnostics Diagnostics { get; init; }
}

/// <summary>A single insulin dose, used to correlate clusters with dosing decisions.</summary>
public sealed record InsulinDose
{
    /// <summary>UTC time the dose was delivered.</summary>
    public required DateTime Time { get; init; }

    /// <summary>Insulin units delivered.</summary>
    public required double Units { get; init; }
}

/// <summary>Options controlling <see cref="SensorIntegrityDetector.FindHypoEvents"/>.</summary>
public sealed record HypoEventOptions
{
    /// <summary>Glucose level (mg/dL) below which a reading counts as hypoglycemic.</summary>
    public double HypoThresholdMgdl { get; init; } = 70.0;

    /// <summary>How many hours after a cluster ends to search for a subsequent hypo nadir.</summary>
    public double WindowHours { get; init; } = 3.0;

    /// <summary>Minimum cluster confidence to consider.</summary>
    public ClusterConfidence MinConfidence { get; init; } = ClusterConfidence.Medium;

    /// <summary>When <c>true</c>, only return events where insulin was dosed during the cluster.</summary>
    public bool RequireInsulin { get; init; } = false;
}

/// <summary>
/// A cluster followed by hypoglycemia within the configured window — the core safety signal:
/// noisy data that may have driven over-dosing into a subsequent low.
/// </summary>
public sealed record HypoEvent
{
    /// <summary>The cluster that preceded the hypo.</summary>
    public required GlucoseCluster Cluster { get; init; }

    /// <summary>Lowest glucose (mg/dL) observed in the post-cluster window.</summary>
    public required double NadirMgdl { get; init; }

    /// <summary>UTC time of the nadir.</summary>
    public required DateTime NadirTime { get; init; }

    /// <summary>Hours from cluster end to the nadir.</summary>
    public required double TimeToNadirHours { get; init; }

    /// <summary>Number of post-cluster readings below the hypo threshold.</summary>
    public required int ReadingsBelowThreshold { get; init; }

    /// <summary>Insulin doses delivered during the cluster window (empty when no insulin data).</summary>
    public required IReadOnlyList<InsulinDose> InsulinDuringCluster { get; init; }
}
