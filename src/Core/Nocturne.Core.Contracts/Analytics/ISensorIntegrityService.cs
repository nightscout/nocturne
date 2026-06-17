using Nocturne.Core.Models.Analytics;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Analyzes a tenant's CGM data for sensor-integrity issues: clusters of directionally-incoherent
/// ("noisy") readings and the cluster-linked hypoglycemia events they may have driven.
/// </summary>
/// <remarks>
/// Orchestrates <see cref="Nocturne.Core.Models.Analytics.SensorIntegrityDetector"/> over data read
/// from the tenant-scoped V4 repositories. The detector is post-hoc and read-only; this service adds
/// no persistence.
/// </remarks>
/// <seealso cref="SensorIntegrityDetector"/>
public interface ISensorIntegrityService
{
    /// <summary>
    /// Run sensor-integrity analysis over the given UTC window.
    /// </summary>
    /// <param name="from">Inclusive UTC start of the window.</param>
    /// <param name="to">Exclusive UTC end of the window.</param>
    /// <param name="source">Optional data source filter; <c>null</c> analyzes the combined
    /// deduplicated stream.</param>
    /// <param name="bySource">When <c>true</c>, also compute a per-data-source breakdown.</param>
    /// <param name="hypoOptions">Hypo-event search options; defaults applied when <c>null</c>.</param>
    /// <param name="config">Detector configuration; defaults applied when <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SensorIntegrityReport> AnalyzeAsync(
        DateTime from,
        DateTime to,
        string? source = null,
        bool bySource = false,
        HypoEventOptions? hypoOptions = null,
        DetectorConfig? config = null,
        CancellationToken ct = default);
}
