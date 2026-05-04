using Nocturne.API.Services.AidDetection;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Service for computing <see cref="AidSystemMetrics"/> from device segment and closed-loop data.
/// Dispatches metric calculations to the registered <see cref="IAidDetectionStrategy"/> for the
/// detected <see cref="AidAlgorithm"/> (AndroidAPS, Trio, Loop, etc.).
/// </summary>
/// <remarks>
/// Each strategy is keyed by the <see cref="AidAlgorithm"/> values it supports.
/// If multiple strategies claim the same algorithm, the last registered one wins.
/// </remarks>
/// <seealso cref="IAidMetricsService"/>
/// <seealso cref="IAidDetectionStrategy"/>
/// <seealso cref="ApsSnapshotStrategy"/>
/// <seealso cref="TbrBasedStrategy"/>
public class AidMetricsService : IAidMetricsService
{
    private readonly Dictionary<AidAlgorithm, IAidDetectionStrategy> _strategies;

    /// <summary>
    /// Initializes a new instance of <see cref="AidMetricsService"/> and builds a strategy
    /// lookup keyed by <see cref="AidAlgorithm"/>.
    /// </summary>
    /// <param name="strategies">All registered <see cref="IAidDetectionStrategy"/> implementations.</param>
    public AidMetricsService(IEnumerable<IAidDetectionStrategy> strategies)
    {
        _strategies = new Dictionary<AidAlgorithm, IAidDetectionStrategy>();
        foreach (var strategy in strategies)
        {
            foreach (var algo in strategy.SupportedAlgorithms)
            {
                _strategies[algo] = strategy;
            }
        }
    }

    public AidSystemMetrics Calculate(
        IReadOnlyList<DeviceSegmentInput> deviceSegments,
        IReadOnlyList<ApsSnapshot> apsSnapshots,
        IReadOnlyList<TempBasal> tempBasals,
        int siteChangeCount,
        string? cgmDeviceNames,
        string? pumpDeviceNames,
        double? cgmActivePercent,
        double? targetLow,
        double? targetHigh,
        DateTime startDate,
        DateTime endDate)
    {
        var result = new AidSystemMetrics
        {
            CgmDeviceNames = cgmDeviceNames,
            PumpDeviceNames = pumpDeviceNames,
            CgmActivePercent = cgmActivePercent,
            TargetLow = targetLow,
            TargetHigh = targetHigh,
            SiteChangeCount = siteChangeCount,
        };

        if (deviceSegments.Count == 0)
            return result;

        double weightedAidActive = 0;
        double weightedPumpUse = 0;
        double aidDurationHours = 0;
        double pumpDurationHours = 0;

        foreach (var segment in deviceSegments)
        {
            // Clamp segment to report bounds
            var segStart = segment.StartDate < startDate ? startDate : segment.StartDate;
            var segEnd = segment.EndDate > endDate ? endDate : segment.EndDate;

            if (segStart >= segEnd)
                continue;

            var segDuration = segEnd - segStart;

            // Resolve strategy
            if (!_strategies.TryGetValue(segment.Algorithm, out var strategy))
                continue;

            // Slice data to segment window
            var segSnapshots = apsSnapshots
                .Where(s => s.Timestamp >= segStart && s.Timestamp < segEnd)
                .ToList();

            var segTempBasals = tempBasals
                .Where(t => t.StartTimestamp < segEnd && (t.EndTimestamp == null || t.EndTimestamp > segStart))
                .ToList();

            var context = new AidDetectionContext
            {
                Algorithm = segment.Algorithm,
                StartDate = segStart,
                EndDate = segEnd,
                ApsSnapshots = segSnapshots,
                TempBasals = segTempBasals,
            };

            var metrics = strategy.CalculateMetrics(context);

            // Build time segment
            result.Segments.Add(new AidTimeSegment
            {
                Algorithm = segment.Algorithm,
                StartDate = segStart,
                EndDate = segEnd,
                DurationHours = segDuration.TotalHours,
                Metrics = metrics,
            });

            // Time-weight the results (only count segments that produced data)
            if (metrics.AidActivePercent.HasValue)
            {
                weightedAidActive += metrics.AidActivePercent.Value * segDuration.TotalHours;
                aidDurationHours += segDuration.TotalHours;
            }

            if (metrics.PumpUsePercent.HasValue)
            {
                weightedPumpUse += metrics.PumpUsePercent.Value * segDuration.TotalHours;
                pumpDurationHours += segDuration.TotalHours;
            }
        }

        if (aidDurationHours > 0)
            result.AidActivePercent = Math.Round(weightedAidActive / aidDurationHours, 1);
        if (pumpDurationHours > 0)
            result.PumpUsePercent = Math.Round(weightedPumpUse / pumpDurationHours, 1);

        return result;
    }
}
